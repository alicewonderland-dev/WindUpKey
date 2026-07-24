using System;
using System.Numerics;
using System.Reflection;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace WindUpKey.Services;

/// <summary>
/// Patch-fragile hooks live only here. When locked (and not in an instance / not yet in-world):
/// suppress walk/fly/turn input (before game processes it — keeps groundsit), hard-freeze facing,
/// block jump (incl. spacebar), block teleport/return, and re-apply groundsit if stood up.
/// Call-travel input mute suppresses player/controller steer and jump without freezing facing or
/// blocking teleport/return, so Lifestream and vnavmesh can still drive the character.
/// Hooks install only while the doll is locked, input-muted, or briefly nudging; they are removed
/// when idle unlocked and on logout. After login/zone/`BetweenAreas`, wait a short settle before
/// install; mid-world unlock nudge / mute installs immediately.
/// </summary>
public sealed unsafe class LockController : IDisposable
{
    // GeneralAction row IDs (Lumina GeneralAction).
    private const uint GeneralActionJump = 2;
    private const uint GeneralActionReturn = 6;
    private const uint GeneralActionTeleport = 7;

    private readonly IPluginLog _log;
    private readonly IGameInteropProvider _interop;
    private readonly IClientState _clientState;
    private readonly ICondition _condition;
    private readonly IObjectTable _objectTable;
    private readonly GameCommandRunner _commands;
    private readonly Configuration _config;

    private bool _locked;
    /// <summary>Mute player/controller steer during owner-call auto-travel (Testing).</summary>
    private bool _inputMute;
    private Vector3? _muteDestination;
    private float _frozenRotation;
    private bool _hasFrozenRotation;
    private bool _applyingFrozenRotation;
    private int _resitCooldownFrames;
    /// <summary>Remaining RMI frames to force forward walk (cancels sit/emotes on unlock).</summary>
    private int _nudgeForwardTicks;
    /// <summary>Deferred <c>/sit</c> stand (must run on framework tick — wind arrives off-thread).</summary>
    private bool _pendingSitStand;

    private bool _vnavMuteTweaksApplied;
    private bool? _savedVnavIgnoreUserInput;
    private bool? _savedVnavCancelOnUserInput;
    private PropertyInfo? _vnavIgnoreUserInputProp;
    private PropertyInfo? _vnavUserInputProp;
    private PropertyInfo? _vnavCancelOnUserInputProp;
    private object? _vnavOverrideMovement;
    private object? _vnavConfig;

    private Hook<RMIWalkDelegate>? _rmiWalkHook;
    private Hook<RMIFlyDelegate>? _rmiFlyHook;
    private Hook<UseActionDelegate>? _useActionHook;
    private Hook<SetRotationDelegate>? _setRotationHook;
    private Hook<IsInputIdDelegate>? _isInputIdPressedHook;
    private Hook<IsInputIdDelegate>? _isInputIdDownHook;
    private Hook<IsInputIdDelegate>? _isInputIdHeldHook;

    private delegate void RMIWalkDelegate(
        void* self,
        float* sumLeft,
        float* sumForward,
        float* sumTurnLeft,
        byte* haveBackwardOrStrafe,
        byte* a6,
        byte bAdditiveUnk);

    private delegate void RMIFlyDelegate(void* self, void* flyInput);

    private delegate bool UseActionDelegate(
        ActionManager* actionManager,
        ActionType actionType,
        uint actionId,
        ulong targetId,
        uint extraParam,
        ActionManager.UseActionMode mode,
        uint comboRouteId,
        bool* outOptAreaTargeted);

    private delegate void SetRotationDelegate(CSGameObject* self, float rotation);

    private delegate bool IsInputIdDelegate(InputData* self, InputId inputId);

    private bool _hooksInstalled;
    private int _hookSettleFrames;
    /// <summary>
    /// True once we have been hook-eligible (logged in, LocalPlayer, not duty/BetweenAreas)
    /// without leaving that state. Used to skip the post-zone settle on mid-world unwind.
    /// </summary>
    private bool _hooksWereEligible;

    public LockController(
        IGameInteropProvider interop,
        IClientState clientState,
        ICondition condition,
        IObjectTable objectTable,
        GameCommandRunner commands,
        Configuration config,
        IPluginLog log)
    {
        _interop = interop;
        _clientState = clientState;
        _condition = condition;
        _objectTable = objectTable;
        _commands = commands;
        _config = config;
        _log = log;
        // Do not install hooks here — title/char-select detours cause spin/crash even when inactive.
    }

    public void SetLocked(bool locked)
    {
        if (!locked)
        {
            _hasFrozenRotation = false;
            _resitCooldownFrames = 0;
        }

        _locked = locked;
        // Do not read ObjectTable here — SetLocked can run during plugin ctor off the main thread.
        // Facing is captured on the next Framework Tick when RestrictionsActive.
    }

    /// <summary>
    /// Mute player and controller steer/jump during owner-call auto-travel without freezing facing
    /// or blocking teleport/return (Lifestream / vnavmesh keep control).
    /// </summary>
    public void SetInputMute(bool mute, Vector3? destination = null)
    {
        if (mute)
            _muteDestination = destination ?? _muteDestination;

        if (mute == _inputMute)
            return;

        _inputMute = mute;
        if (mute)
            ApplyVnavMuteTweaks();
        else
        {
            _muteDestination = null;
            RestoreVnavMuteTweaks();
        }
    }

    /// <summary>Update the point input-mute walk injection steers toward while muted.</summary>
    public void SetInputMuteDestination(Vector3 destination) => _muteDestination = destination;

    /// <summary>
    /// After rewind: stand from sit/groundsit via <c>/sit</c> (get-up anim), or cancel other
    /// looping emotes with one frame of forward walk. No-op when already idle.
    /// Sit is queued for the next framework tick — inbound wind runs off-thread.
    /// </summary>
    public void RequestCancelPoseNudge()
    {
        if (_objectTable.LocalPlayer is null)
            return;

        if (ShouldStandWithSitCommand())
        {
            _pendingSitStand = true;
            return;
        }

        if (!_condition[ConditionFlag.InThatPosition] && !_condition[ConditionFlag.Emoting])
            return;

        _nudgeForwardTicks = 1;
    }

    /// <summary>
    /// Sit / groundsit (configured lock emote or detected pose) → use <c>/sit</c> for get-up anim.
    /// </summary>
    private unsafe bool ShouldStandWithSitCommand()
    {
        if (!_condition[ConditionFlag.InThatPosition])
            return false;

        var lockId = _config.EffectiveLockEmoteId;
        if (lockId is GameCommandRunner.SitEmoteId or GameCommandRunner.GroundSitEmoteId)
            return true;

        return IsSittingOrGroundSitting();
    }

    /// <summary>
    /// True when local player is in chair-sit or groundsit (not doze / other position loops).
    /// </summary>
    private unsafe bool IsSittingOrGroundSitting()
    {
        var player = _objectTable.LocalPlayer;
        if (player is null)
            return false;

        try
        {
            var character = (Character*)player.Address;
            var emoteId = character->EmoteController.EmoteId;
            if (emoteId is GameCommandRunner.SitEmoteId or GameCommandRunner.GroundSitEmoteId)
                return true;

            var pose = character->EmoteController.CurrentPoseType;
            if (pose is EmoteController.PoseType.Sit or EmoteController.PoseType.GroundSit)
                return true;

            // EmoteMode rows: 1 = groundsit, 2 = sit (doze is 3).
            if (character->Mode == CharacterModes.InPositionLoop && character->ModeParam is 1 or 2)
                return true;

            return false;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "WindUpKey: failed to detect sit/groundsit state");
            return false;
        }
    }

    /// <summary>Call each framework tick to keep facing frozen and groundsit enforced while restricted.</summary>
    public void Tick()
    {
        if (_pendingSitStand)
        {
            _pendingSitStand = false;
            if (_objectTable.LocalPlayer is not null)
                _commands.Execute("/sit");
        }

        EnsureHooksInstalled();

        // Hooks are only needed while locked, input-muted, or briefly for an unlock pose nudge.
        if (_hooksInstalled && !_locked && !_inputMute && _nudgeForwardTicks <= 0)
            UninstallHooks();

        if (!RestrictionsActive)
        {
            // Recapture facing when leaving a duty while still locked.
            _hasFrozenRotation = false;
            _resitCooldownFrames = 0;
            return;
        }

        if (!_hasFrozenRotation)
            TryCaptureRotation();

        ApplyFrozenRotation();
        EnforceGroundSit();
    }

    /// <summary>
    /// True when doll lock should suppress input. Requires a real login plus LocalPlayer so we never
    /// intercept RMI/input on title, character select, or login load (that caused spin/crash).
    /// Also off inside a duty/instance and during zone transitions.
    /// </summary>
    private bool RestrictionsActive =>
        _locked
        && _clientState.IsLoggedIn
        && !IsInInstance()
        && _objectTable.LocalPlayer is not null;

    /// <summary>Call-travel mute: in-world only, same safety gates as lock (no title/char-select).</summary>
    private bool InputMuteActive =>
        _inputMute
        && _clientState.IsLoggedIn
        && !IsInInstance()
        && _objectTable.LocalPlayer is not null;

    private bool IsInInstance()
    {
        return _condition[ConditionFlag.BoundByDuty]
               || _condition[ConditionFlag.BoundByDuty56]
               || _condition[ConditionFlag.BoundByDuty95]
               || _condition[ConditionFlag.BetweenAreas]
               || _condition[ConditionFlag.BetweenAreas51];
    }

    /// <summary>
    /// Install movement hooks only when the doll is locked and fully in-world
    /// (or briefly while a cancel-pose forward nudge is pending).
    /// Settle (~3s) only after first becoming eligible post-login/zone; mid-world unwind
    /// and unlock nudge install immediately. Passthrough hooks around login still crash.
    /// </summary>
    private void EnsureHooksInstalled()
    {
        if (_hooksInstalled)
            return;

        var eligible = _clientState.IsLoggedIn
                       && _objectTable.LocalPlayer is not null
                       && !IsInInstance();
        if (!eligible)
        {
            _hookSettleFrames = 0;
            _hooksWereEligible = false;
            return;
        }

        var needHooks = _locked || _inputMute || _nudgeForwardTicks > 0;
        if (!needHooks)
        {
            // Still in-world while wound — keep eligibility so the next lock skips settle.
            _hookSettleFrames = 0;
            _hooksWereEligible = true;
            return;
        }

        // Already in-world (or unlock nudge / mute): install immediately.
        if (_hooksWereEligible || ((_nudgeForwardTicks > 0 || _inputMute) && !_locked))
        {
            _hooksWereEligible = true;
            InstallHooksNow();
            return;
        }

        // Just became eligible after login / BetweenAreas / duty — wait before install.
        _hookSettleFrames++;
        if (_hookSettleFrames < 180) // ~3s at 60fps after leaving BetweenAreas
            return;

        _hooksWereEligible = true;
        InstallHooksNow();
    }

    private void InstallHooksNow()
    {
        TryInstallHooks();
        _hooksInstalled = _rmiWalkHook is not null
            || _rmiFlyHook is not null
            || _useActionHook is not null
            || _setRotationHook is not null
            || _isInputIdPressedHook is not null;
    }

    /// <summary>
    /// Drop hooks when no longer needed (unlocked) or on logout.
    /// Does not clear <see cref="_hooksWereEligible"/> — mid-world unlock must not re-settle on the next lock.
    /// </summary>
    public void UninstallHooks()
    {
        RestoreVnavMuteTweaks();
        DisposeHooks();
        _hooksInstalled = false;
        _hookSettleFrames = 0;
        _hasFrozenRotation = false;
        _resitCooldownFrames = 0;
        _nudgeForwardTicks = 0;
        _pendingSitStand = false;
    }

    private void TryInstallHooks()
    {
        try
        {
            _rmiWalkHook = _interop.HookFromSignature<RMIWalkDelegate>(
                "E8 ?? ?? ?? ?? 80 7B 3E 00 48 8D 3D",
                RMIWalkDetour);
            _rmiWalkHook.Enable();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "WindUpKey: failed to hook RMI walk (movement lock may be incomplete this patch)");
        }

        try
        {
            _rmiFlyHook = _interop.HookFromSignature<RMIFlyDelegate>(
                "E8 ?? ?? ?? ?? 0F B6 0D ?? ?? ?? ?? B8",
                RMIFlyDetour);
            _rmiFlyHook.Enable();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "WindUpKey: failed to hook RMI fly");
        }

        try
        {
            _useActionHook = _interop.HookFromAddress<UseActionDelegate>(
                ActionManager.MemberFunctionPointers.UseAction,
                UseActionDetour);
            _useActionHook.Enable();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "WindUpKey: failed to hook UseAction (teleport/jump lock may be incomplete)");
        }

        try
        {
            _setRotationHook = _interop.HookFromAddress<SetRotationDelegate>(
                CSGameObject.MemberFunctionPointers.SetRotation,
                SetRotationDetour);
            _setRotationHook.Enable();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "WindUpKey: failed to hook SetRotation (LMB+RMB turn lock may be incomplete)");
        }

        // Spacebar / pad jump is InputId.JUMP — may not always go through UseAction.
        try
        {
            _isInputIdPressedHook = _interop.HookFromAddress<IsInputIdDelegate>(
                InputData.MemberFunctionPointers.IsInputIdPressed,
                IsInputIdPressedDetour);
            _isInputIdPressedHook.Enable();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "WindUpKey: failed to hook IsInputIdPressed (spacebar jump lock may be incomplete)");
        }

        try
        {
            _isInputIdDownHook = _interop.HookFromAddress<IsInputIdDelegate>(
                InputData.MemberFunctionPointers.IsInputIdDown,
                IsInputIdDownDetour);
            _isInputIdDownHook.Enable();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "WindUpKey: failed to hook IsInputIdDown");
        }

        try
        {
            _isInputIdHeldHook = _interop.HookFromAddress<IsInputIdDelegate>(
                InputData.MemberFunctionPointers.IsInputIdHeld,
                IsInputIdHeldDetour);
            _isInputIdHeldHook.Enable();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "WindUpKey: failed to hook IsInputIdHeld");
        }
    }

    private void DisposeHooks()
    {
        _rmiWalkHook?.Dispose();
        _rmiWalkHook = null;
        _rmiFlyHook?.Dispose();
        _rmiFlyHook = null;
        _useActionHook?.Dispose();
        _useActionHook = null;
        _setRotationHook?.Dispose();
        _setRotationHook = null;
        _isInputIdPressedHook?.Dispose();
        _isInputIdPressedHook = null;
        _isInputIdDownHook?.Dispose();
        _isInputIdDownHook = null;
        _isInputIdHeldHook?.Dispose();
        _isInputIdHeldHook = null;
    }

    private void RMIWalkDetour(
        void* self,
        float* sumLeft,
        float* sumForward,
        float* sumTurnLeft,
        byte* haveBackwardOrStrafe,
        byte* a6,
        byte bAdditiveUnk)
    {
        // Must not call Original while restricted: it consumes LMB+RMB/WASD and cancels groundsit
        // before any post-zeroing of the float outputs.
        if (RestrictionsActive)
        {
            *sumLeft = 0;
            *sumForward = 0;
            *sumTurnLeft = 0;
            if (haveBackwardOrStrafe != null)
                *haveBackwardOrStrafe = 0;
            if (a6 != null)
                *a6 = 0;

            if (!_hasFrozenRotation)
                TryCaptureRotation();
            ApplyFrozenRotation();
            return;
        }

        _rmiWalkHook!.Original(self, sumLeft, sumForward, sumTurnLeft, haveBackwardOrStrafe, a6, bAdditiveUnk);

        if (InputMuteActive)
        {
            // Strip keyboard and controller steer aggregated into RMI, then re-apply walk toward
            // the call destination so vnav/Lifestream are not fighting player input.
            *sumLeft = 0;
            *sumForward = 0;
            *sumTurnLeft = 0;
            if (haveBackwardOrStrafe != null)
                *haveBackwardOrStrafe = 0;
            if (a6 != null)
                *a6 = 0;

            TryInjectMuteWalk(sumLeft, sumForward);
            TryClearVnavUserInputFlag();
            return;
        }

        // One frame of forward input cancels sit / looping emotes after rewind.
        if (_nudgeForwardTicks > 0)
        {
            *sumForward = 1f;
            *sumLeft = 0;
            *sumTurnLeft = 0;
            _nudgeForwardTicks--;
        }
    }

    private void RMIFlyDetour(void* self, void* flyInput)
    {
        if (RestrictionsActive)
        {
            if (flyInput != null)
            {
                var floats = (float*)flyInput;
                for (var i = 0; i < 6; i++)
                    floats[i] = 0;
            }

            if (!_hasFrozenRotation)
                TryCaptureRotation();
            ApplyFrozenRotation();
            return;
        }

        _rmiFlyHook!.Original(self, flyInput);

        if (InputMuteActive && flyInput != null)
        {
            var floats = (float*)flyInput;
            for (var i = 0; i < 6; i++)
                floats[i] = 0;
            TryClearVnavUserInputFlag();
        }
    }

    private bool UseActionDetour(
        ActionManager* actionManager,
        ActionType actionType,
        uint actionId,
        ulong targetId,
        uint extraParam,
        ActionManager.UseActionMode mode,
        uint comboRouteId,
        bool* outOptAreaTargeted)
    {
        // Input mute must not block teleport/return — Lifestream needs them.
        if (RestrictionsActive && IsRestrictedAction(actionType, actionId))
            return false;

        if (InputMuteActive && IsJumpAction(actionType, actionId))
            return false;

        return _useActionHook!.Original(
            actionManager, actionType, actionId, targetId, extraParam, mode, comboRouteId, outOptAreaTargeted);
    }

    private void SetRotationDetour(CSGameObject* self, float rotation)
    {
        if (_setRotationHook is null)
            return;

        if (!_applyingFrozenRotation
            && RestrictionsActive
            && _hasFrozenRotation
            && IsLocalPlayer(self))
        {
            _setRotationHook.Original(self, _frozenRotation);
            return;
        }

        _setRotationHook.Original(self, rotation);
    }

    private bool IsInputIdPressedDetour(InputData* self, InputId inputId)
    {
        if ((RestrictionsActive || InputMuteActive) && IsJumpInput(inputId))
            return false;
        return _isInputIdPressedHook!.Original(self, inputId);
    }

    private bool IsInputIdDownDetour(InputData* self, InputId inputId)
    {
        if ((RestrictionsActive || InputMuteActive) && IsJumpInput(inputId))
            return false;
        return _isInputIdDownHook!.Original(self, inputId);
    }

    private bool IsInputIdHeldDetour(InputData* self, InputId inputId)
    {
        if ((RestrictionsActive || InputMuteActive) && IsJumpInput(inputId))
            return false;
        return _isInputIdHeldHook!.Original(self, inputId);
    }

    private void EnforceGroundSit()
    {
        if (!_config.AutoGroundSit)
            return;

        if (_resitCooldownFrames > 0)
        {
            _resitCooldownFrames--;
            return;
        }

        // Groundsit / playdead sets InThatPosition; if they stood up, put them back.
        if (_condition[ConditionFlag.InThatPosition] || _condition[ConditionFlag.Emoting])
            return;

        if (_objectTable.LocalPlayer is null)
            return;

        if (_commands.TryExecuteLockEmote())
            _resitCooldownFrames = 90; // ~1.5s at 60fps — avoid command spam while standing anim plays
    }

    private static bool IsJumpInput(InputId inputId) =>
        inputId is InputId.JUMP or InputId.PAD_JUMPANDCANCELCAST;

    private static bool IsJumpAction(ActionType actionType, uint actionId)
    {
        if (actionType == ActionType.GeneralAction)
            return actionId == GeneralActionJump;
        if (actionType == ActionType.Action)
            return actionId == 5; // Jump
        return false;
    }

    private static bool IsRestrictedAction(ActionType actionType, uint actionId)
    {
        if (actionType == ActionType.GeneralAction)
            return actionId is GeneralActionJump or GeneralActionTeleport or GeneralActionReturn;

        if (actionType == ActionType.Action)
            return actionId is 5 or 6 or 7;

        return false;
    }

    private void TryInjectMuteWalk(float* sumLeft, float* sumForward)
    {
        if (_muteDestination is not { } dest)
            return;

        var player = _objectTable.LocalPlayer;
        if (player is null)
            return;

        var delta = dest - player.Position;
        delta.Y = 0;
        if (delta.LengthSquared() < 0.05f * 0.05f)
            return;

        var desired = MathF.Atan2(delta.X, delta.Z);
        var rel = desired - player.Rotation;
        // Wrap to [-pi, pi]
        while (rel > MathF.PI)
            rel -= MathF.PI * 2;
        while (rel < -MathF.PI)
            rel += MathF.PI * 2;

        *sumLeft = MathF.Sin(rel);
        *sumForward = MathF.Cos(rel);
    }

    private void ApplyVnavMuteTweaks()
    {
        if (_vnavMuteTweaksApplied)
            return;

        try
        {
            EnsureVnavReflection();
            if (_vnavOverrideMovement is not null && _vnavIgnoreUserInputProp is not null)
            {
                _savedVnavIgnoreUserInput = _vnavIgnoreUserInputProp.GetValue(_vnavOverrideMovement) is bool ignore ? ignore : null;
                _vnavIgnoreUserInputProp.SetValue(_vnavOverrideMovement, true);
            }

            if (_vnavConfig is not null && _vnavCancelOnUserInputProp is not null)
            {
                _savedVnavCancelOnUserInput = _vnavCancelOnUserInputProp.GetValue(_vnavConfig) is bool cancel ? cancel : null;
                _vnavCancelOnUserInputProp.SetValue(_vnavConfig, false);
            }

            TryClearVnavUserInputFlag();
            _vnavMuteTweaksApplied = true;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "WindUpKey: vnavmesh mute tweaks unavailable");
        }
    }

    private void RestoreVnavMuteTweaks()
    {
        if (!_vnavMuteTweaksApplied)
            return;

        try
        {
            if (_vnavOverrideMovement is not null && _vnavIgnoreUserInputProp is not null && _savedVnavIgnoreUserInput is { } ignore)
                _vnavIgnoreUserInputProp.SetValue(_vnavOverrideMovement, ignore);

            if (_vnavConfig is not null && _vnavCancelOnUserInputProp is not null && _savedVnavCancelOnUserInput is { } cancel)
                _vnavCancelOnUserInputProp.SetValue(_vnavConfig, cancel);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "WindUpKey: failed restoring vnavmesh mute tweaks");
        }
        finally
        {
            _vnavMuteTweaksApplied = false;
            _savedVnavIgnoreUserInput = null;
            _savedVnavCancelOnUserInput = null;
        }
    }

    private void TryClearVnavUserInputFlag()
    {
        try
        {
            EnsureVnavReflection();
            if (_vnavOverrideMovement is not null)
                _vnavUserInputProp?.SetValue(_vnavOverrideMovement, false);
        }
        catch
        {
            // optional
        }
    }

    private void EnsureVnavReflection()
    {
        if (_vnavOverrideMovement is not null && _vnavConfig is not null)
            return;

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = asm.GetName().Name;
            if (name is not ("vnavmesh" or "ffxiv_navmesh"))
                continue;

            Type? configType = null;
            foreach (var type in asm.GetTypes())
            {
                if (type.Name == "OverrideMovement")
                {
                    _vnavIgnoreUserInputProp ??= type.GetProperty("IgnoreUserInput", BindingFlags.Instance | BindingFlags.Public);
                    _vnavUserInputProp ??= type.GetProperty("UserInput", BindingFlags.Instance | BindingFlags.Public);
                }

                if ((type.Name is "Config" or "Configuration") && _vnavCancelOnUserInputProp is null)
                {
                    var prop = type.GetProperty("CancelMoveOnUserInput", BindingFlags.Instance | BindingFlags.Public);
                    if (prop is null)
                        continue;
                    _vnavCancelOnUserInputProp = prop;
                    configType = type;
                }
            }

            if (configType is not null && _vnavConfig is null)
            {
                foreach (var t in asm.GetTypes())
                {
                    if (t.Name != "Service")
                        continue;
                    var cfgProp = t.GetProperty("Config", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (cfgProp?.GetValue(null) is { } cfg && configType.IsInstanceOfType(cfg))
                    {
                        _vnavConfig = cfg;
                        break;
                    }
                }
            }

            if (_vnavOverrideMovement is null)
            {
                foreach (var type in asm.GetTypes())
                {
                    if (type.Name is not ("Service" or "Plugin"))
                        continue;

                    foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        object? root;
                        try
                        {
                            root = field.GetValue(null);
                        }
                        catch
                        {
                            continue;
                        }

                        if (root is not null && TryFindOverrideMovement(root, depth: 0, out var found))
                        {
                            _vnavOverrideMovement = found;
                            _vnavIgnoreUserInputProp ??= found.GetType().GetProperty("IgnoreUserInput", BindingFlags.Instance | BindingFlags.Public);
                            _vnavUserInputProp ??= found.GetType().GetProperty("UserInput", BindingFlags.Instance | BindingFlags.Public);
                            break;
                        }
                    }

                    if (_vnavOverrideMovement is not null)
                        break;
                }
            }

            break;
        }
    }

    private static bool TryFindOverrideMovement(object root, int depth, out object found)
    {
        found = null!;
        if (depth > 5)
            return false;

        if (root.GetType().Name == "OverrideMovement")
        {
            found = root;
            return true;
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var field in root.GetType().GetFields(flags))
        {
            object? child;
            try
            {
                child = field.GetValue(root);
            }
            catch
            {
                continue;
            }

            if (child is null || child is string || child.GetType().IsPrimitive)
                continue;

            if (child.GetType().Namespace?.StartsWith("System", StringComparison.Ordinal) == true
                && child.GetType().Name is not ("FollowPath" or "OverrideMovement"))
                continue;

            if (TryFindOverrideMovement(child, depth + 1, out found))
                return true;
        }

        return false;
    }

    private bool IsLocalPlayer(CSGameObject* self)
    {
        if (self == null)
            return false;

        var player = _objectTable.LocalPlayer;
        if (player is null)
            return false;

        return (CSGameObject*)player.Address == self;
    }

    private void TryCaptureRotation()
    {
        var player = _objectTable.LocalPlayer;
        if (player is null)
            return;

        _frozenRotation = player.Rotation;
        _hasFrozenRotation = true;
    }

    private void ApplyFrozenRotation()
    {
        if (!_hasFrozenRotation || _setRotationHook is null)
            return;

        var player = _objectTable.LocalPlayer;
        if (player is null)
            return;

        var gameObject = (CSGameObject*)player.Address;
        if (gameObject == null)
            return;

        if (gameObject->Rotation == _frozenRotation)
            return;

        _applyingFrozenRotation = true;
        try
        {
            _setRotationHook.Original(gameObject, _frozenRotation);
        }
        finally
        {
            _applyingFrozenRotation = false;
        }
    }

    public void Dispose() => UninstallHooks();
}
