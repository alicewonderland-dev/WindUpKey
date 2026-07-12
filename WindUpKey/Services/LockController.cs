using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace WindUpKey.Services;

/// <summary>
/// Patch-fragile hooks live only here. When locked (and not in an instance / not yet in-world):
/// suppress walk/fly/turn input (before game processes it — keeps groundsit), hard-freeze facing,
/// block jump (incl. spacebar), block teleport/return, and re-apply groundsit if stood up.
/// Restrictions stay off until LocalPlayer exists so title/char-select never hit the detours.
/// </summary>
public sealed unsafe class LockController : IDisposable
{
    // GeneralAction row IDs (Lumina GeneralAction).
    private const uint GeneralActionJump = 2;
    private const uint GeneralActionReturn = 6;
    private const uint GeneralActionTeleport = 7;

    private readonly IPluginLog _log;
    private readonly IGameInteropProvider _interop;
    private readonly ICondition _condition;
    private readonly IObjectTable _objectTable;
    private readonly GameCommandRunner _commands;
    private readonly Configuration _config;

    private bool _locked;
    private float _frozenRotation;
    private bool _hasFrozenRotation;
    private bool _applyingFrozenRotation;
    private int _resitCooldownFrames;

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

    public LockController(
        IGameInteropProvider interop,
        ICondition condition,
        IObjectTable objectTable,
        GameCommandRunner commands,
        Configuration config,
        IPluginLog log)
    {
        _interop = interop;
        _condition = condition;
        _objectTable = objectTable;
        _commands = commands;
        _config = config;
        _log = log;
        TryInstallHooks();
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

    /// <summary>Call each framework tick to keep facing frozen and groundsit enforced while restricted.</summary>
    public void Tick()
    {
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
    /// True when doll lock should suppress input. Requires an in-world LocalPlayer so we never
    /// intercept RMI/input on title, character select, or login load (that caused spin/crash).
    /// Also off inside a duty/instance and during zone transitions.
    /// </summary>
    private bool RestrictionsActive =>
        _locked
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
        if (RestrictionsActive && IsRestrictedAction(actionType, actionId))
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
        if (RestrictionsActive && IsJumpInput(inputId))
            return false;
        return _isInputIdPressedHook!.Original(self, inputId);
    }

    private bool IsInputIdDownDetour(InputData* self, InputId inputId)
    {
        if (RestrictionsActive && IsJumpInput(inputId))
            return false;
        return _isInputIdDownHook!.Original(self, inputId);
    }

    private bool IsInputIdHeldDetour(InputData* self, InputId inputId)
    {
        if (RestrictionsActive && IsJumpInput(inputId))
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

        // Groundsit sets InThatPosition; if they stood up, put them back.
        if (_condition[ConditionFlag.InThatPosition] || _condition[ConditionFlag.Emoting])
            return;

        if (_objectTable.LocalPlayer is null)
            return;

        if (_commands.TryExecute(GameCommandRunner.SitGround))
            _resitCooldownFrames = 90; // ~1.5s at 60fps — avoid command spam while standing anim plays
    }

    private static bool IsJumpInput(InputId inputId) =>
        inputId is InputId.JUMP or InputId.PAD_JUMPANDCANCELCAST;

    private static bool IsRestrictedAction(ActionType actionType, uint actionId)
    {
        if (actionType == ActionType.GeneralAction)
            return actionId is GeneralActionJump or GeneralActionTeleport or GeneralActionReturn;

        if (actionType == ActionType.Action)
            return actionId is 5 or 6 or 7;

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

    public void Dispose()
    {
        _rmiWalkHook?.Dispose();
        _rmiFlyHook?.Dispose();
        _useActionHook?.Dispose();
        _setRotationHook?.Dispose();
        _isInputIdPressedHook?.Dispose();
        _isInputIdDownHook?.Dispose();
        _isInputIdHeldHook?.Dispose();
    }
}
