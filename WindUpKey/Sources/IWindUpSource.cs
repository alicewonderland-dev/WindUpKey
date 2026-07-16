using System;

namespace WindUpKey.Sources;

/// <summary>
/// Wind entry point. Remote player-driven sources use RelayClient; local game-event sources may use WindTimerService.
/// Never manipulate expiry or locks directly.
/// </summary>
public interface IWindUpSource : IDisposable
{
    void Enable();
}
