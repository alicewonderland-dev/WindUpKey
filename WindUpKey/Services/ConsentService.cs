namespace WindUpKey.Services;

public sealed class ConsentService(Configuration config)
{
    public bool IsPairedByKey(string pairingKey) => config.IsPairedByKey(pairingKey);

    /// <summary>Receiving doll: paired partner (by pairing key) with CanWindMe or IsOwner.</summary>
    public bool CanReceiveWindFromKey(string fromPairingKey)
    {
        var pair = config.FindPairByKey(fromPairingKey);
        return pair is { CanWindMe: true } or { IsOwner: true };
    }

    /// <summary>Receiving doll: paired partner (by pairing key) with CanUnwindMe or IsOwner.</summary>
    public bool CanReceiveUnwindFromKey(string fromPairingKey)
    {
        var pair = config.FindPairByKey(fromPairingKey);
        return pair is { CanUnwindMe: true } or { IsOwner: true };
    }

    /// <summary>Receiving doll: paired partner marked as owner.</summary>
    public bool IsOwnerKey(string fromPairingKey)
    {
        var pair = config.FindPairByKey(fromPairingKey);
        return pair is { IsOwner: true };
    }

    /// <summary>
    /// Receiving doll: owner may call when Hardcore is on, or when CanCallMe is set for that owner.
    /// </summary>
    public bool CanReceiveCallFromKey(string fromPairingKey)
    {
        var pair = config.FindPairByKey(fromPairingKey);
        if (pair is not { IsOwner: true })
            return false;
        return config.HardcoreMode || pair.CanCallMe;
    }
}
