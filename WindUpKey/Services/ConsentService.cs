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
}
