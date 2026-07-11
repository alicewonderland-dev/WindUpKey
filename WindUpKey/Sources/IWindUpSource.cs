using System;

namespace WindUpKey.Sources;

/// <summary>
/// Wind entry point. Implement Enable/Dispose only; call RelayClient.SendWindAsync — never touch timer/lock directly.
/// </summary>
public interface IWindUpSource : IDisposable
{
    void Enable();
}
