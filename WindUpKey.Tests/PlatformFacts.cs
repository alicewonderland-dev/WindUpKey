using System.Runtime.InteropServices;

namespace WindUpKey.Tests;

/// <summary>
/// A fact that denies file access with Unix file modes (chmod). Skipped on Windows, where access is denied through
/// ACLs instead and file modes aren't supported.
/// </summary>
public sealed class UnixOnlyFactAttribute : FactAttribute
{
    public UnixOnlyFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "Uses Unix file modes to deny read access; not supported on Windows.";
    }
}

/// <summary>
/// A <see cref="UnixOnlyFactAttribute"/> whose test relies on file modes actually denying access. Also skipped when
/// running as root (effective UID 0, for example in a container), since root ignores file modes.
/// </summary>
public sealed class UnixPermissionFactAttribute : FactAttribute
{
    public UnixPermissionFactAttribute()
    {
        Skip = PermissionFacts.SkipReason(OperatingSystem.IsWindows(), PermissionFacts.CurrentEffectiveUserId(), unixOnly: true);
    }
}

/// <summary>
/// A fact on every OS whose test relies on a read-only file refusing writes: on Unix that is a file mode, which root
/// ignores, so it is skipped when running as root there.
/// </summary>
public sealed class NotAsRootFactAttribute : FactAttribute
{
    public NotAsRootFactAttribute()
    {
        Skip = PermissionFacts.SkipReason(OperatingSystem.IsWindows(), PermissionFacts.CurrentEffectiveUserId(), unixOnly: false);
    }
}

/// <summary>
/// A fact that runs a child process under the time zone <paramref name="zone"/> (an IANA id, set through <c>TZ</c>).
/// Skipped on Windows, where .NET doesn't read <c>TZ</c>, and when the zone isn't available on this host.
/// </summary>
public sealed class TimeZoneFactAttribute : FactAttribute
{
    public TimeZoneFactAttribute(string zone)
    {
        Skip = TimeZoneFacts.SkipReason(OperatingSystem.IsWindows(), zone, TimeZoneFacts.IsAvailable);
    }
}

internal static class TimeZoneFacts
{
    /// <summary>Why a test under <paramref name="zone"/> is skipped, or null to run it.</summary>
    public static string? SkipReason(bool isWindows, string zone, Func<string, bool> isAvailable)
    {
        if (isWindows)
            return "Sets the time zone through TZ, which .NET reads only on Unix.";
        if (!isAvailable(zone))
            return $"The time zone {zone} isn't available on this host.";
        return null;
    }

    /// <summary>Whether this host's time zone data has <paramref name="zone"/>.</summary>
    public static bool IsAvailable(string zone) => TimeZoneInfo.TryFindSystemTimeZoneById(zone, out _);
}

internal static class PermissionFacts
{
    /// <summary>
    /// Why a permission-based test is skipped, or null to run it. <paramref name="effectiveUserId"/> is the Unix
    /// effective UID, or null when unknown (then it runs).
    /// </summary>
    public static string? SkipReason(bool isWindows, uint? effectiveUserId, bool unixOnly)
    {
        if (isWindows)
            return unixOnly ? "Uses Unix file modes to deny access; not supported on Windows." : null;
        if (effectiveUserId == 0)
            return "Relies on file modes denying access; root ignores them.";
        return null;
    }

    /// <summary>The process's effective UID on Unix, or null on Windows or if it can't be read.</summary>
    public static uint? CurrentEffectiveUserId() => OperatingSystem.IsWindows() ? null : EffectiveUserId();

    /// <summary>The process's effective UID (geteuid), or null if it can't be read.</summary>
    public static uint? EffectiveUserId()
    {
        try
        {
            return geteuid();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint geteuid();
}
