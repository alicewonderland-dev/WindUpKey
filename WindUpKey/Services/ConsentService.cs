using System;
using System.Linq;
using WindUpKey.Protocol;

namespace WindUpKey.Services;

public sealed class ConsentService(Configuration config)
{
    public bool IsAllowed(string fromIdentity)
    {
        if (!config.WhitelistEnabled)
            return true;

        var normalized = PlayerIdentity.Normalize(fromIdentity);
        return config.Whitelist.Any(entry =>
            string.Equals(PlayerIdentity.Normalize(entry), normalized, StringComparison.OrdinalIgnoreCase));
    }
}
