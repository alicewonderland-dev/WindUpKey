using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace WindUpKey.Services;

/// <summary>
/// Patch-fragile hooks live only here. When locked: suppress walk/fly input and block teleport/return actions.
/// </summary>
public sealed unsafe class LockController : IDisposable
{
    // GeneralAction row IDs (Lumina GeneralAction).
    private const uint GeneralActionReturn = 6;
    private const uint GeneralActionTeleport = 7;

    private readonly IPluginLog _log;
    private readonly IGameInteropProvider _interop;

    private bool _locked;
    private Hook<RMIWalkDelegate>? _rmiWalkHook;
    private Hook<RMIFlyDelegate>? _rmiFlyHook;
    private Hook<UseActionDelegate>? _useActionHook;

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

    public LockController(IGameInteropProvider interop, IPluginLog log)
    {
        _interop = interop;
        _log = log;
        TryInstallHooks();
    }

    public void SetLocked(bool locked) => _locked = locked;

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
            _log.Warning(ex, "WindUpKey: failed to hook UseAction (teleport lock may be incomplete)");
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
        _rmiWalkHook!.Original(self, sumLeft, sumForward, sumTurnLeft, haveBackwardOrStrafe, a6, bAdditiveUnk);
        if (!_locked)
            return;

        *sumLeft = 0;
        *sumForward = 0;
        *sumTurnLeft = 0;
        if (haveBackwardOrStrafe != null)
            *haveBackwardOrStrafe = 0;
    }

    private void RMIFlyDetour(void* self, void* flyInput)
    {
        _rmiFlyHook!.Original(self, flyInput);
        if (!_locked || flyInput == null)
            return;

        // PlayerMoveControllerFlyInput: first floats are directional; zero a conservative block of floats.
        var floats = (float*)flyInput;
        for (var i = 0; i < 6; i++)
            floats[i] = 0;
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
        if (_locked && IsTeleportRelated(actionType, actionId))
            return false;

        return _useActionHook!.Original(
            actionManager, actionType, actionId, targetId, extraParam, mode, comboRouteId, outOptAreaTargeted);
    }

    private static bool IsTeleportRelated(ActionType actionType, uint actionId)
    {
        if (actionType == ActionType.GeneralAction)
            return actionId is GeneralActionTeleport or GeneralActionReturn;

        // Common Action-row teleports / returns (may expand later).
        // 7 = Teleport (when adjusted as Action in some paths), 6 = Return — kept as belt-and-suspenders.
        if (actionType == ActionType.Action)
            return actionId is 5 or 6 or 7;

        return false;
    }

    public void Dispose()
    {
        _rmiWalkHook?.Dispose();
        _rmiFlyHook?.Dispose();
        _useActionHook?.Dispose();
    }
}
