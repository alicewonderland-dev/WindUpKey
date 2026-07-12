using WindUpKey.Protocol;

namespace WindUpKey.Services;

public sealed class ConsentService(Configuration config)
{
    public bool IsPaired(string identity) => config.IsPaired(identity);

    public bool IsPairedByKey(string pairingKey) => config.IsPairedByKey(pairingKey);

    /// <summary>Receiving doll: paired partner (by Name@World) with CanWindMe enabled.</summary>
    public bool CanReceiveWindFrom(string fromIdentity)
    {
        var pair = config.FindPair(fromIdentity);
        return pair is { CanWindMe: true };
    }

    /// <summary>Receiving doll: paired partner (by pairing key) with CanWindMe enabled.</summary>
    public bool CanReceiveWindFromKey(string fromPairingKey)
    {
        var pair = config.FindPairByKey(fromPairingKey);
        return pair is { CanWindMe: true };
    }

    /// <summary>Receiving doll: paired partner (by pairing key) with CanUnwindMe enabled.</summary>
    public bool CanReceiveUnwindFromKey(string fromPairingKey)
    {
        var pair = config.FindPairByKey(fromPairingKey);
        return pair is { CanUnwindMe: true };
    }
}
