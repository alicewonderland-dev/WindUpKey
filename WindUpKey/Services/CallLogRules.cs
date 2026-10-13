#if WINDUP_TESTING || WINDUP_CALL_UNITS
namespace WindUpKey.Services;

/// <summary>
/// 07-A8 (D11): the Call log. A forced line (written with debug mode off, so a failure always leaves a report) carries
/// only the reason, the phase, a coarse kind and an exception's type: never coordinates, an address, a world, a
/// territory or an exception's message, and never a key or ContentId (04-D10). Location detail goes in debug lines only.
/// <c>CallTravel.debug.log</c> rotates at <see cref="RotateAtBytes"/>, keeping one <see cref="RotatedSuffix"/> file.
/// No Dalamud dependency.
/// </summary>
public static class CallLogRules
{
    /// <summary>The log is rotated before a line would take it past this size.</summary>
    public const long RotateAtBytes = 1024 * 1024;

    /// <summary>The one older file kept: <c>CallTravel.debug.log.1</c>.</summary>
    public const string RotatedSuffix = ".1";

    /// <summary>True when the file (<paramref name="currentBytes"/>) must be rotated before appending <paramref name="appendBytes"/>.</summary>
    public static bool ShouldRotate(long currentBytes, long appendBytes) =>
        currentBytes > 0 && currentBytes + appendBytes > RotateAtBytes;

    /// <summary>A forced line: <paramref name="reason"/> (location-free text), the phase, the coarse kind and an exception type.</summary>
    public static string Forced(string reason, CallPhase phase, string kind, string? exceptionType = null) =>
        exceptionType is null
            ? $"{reason} | phase={phase} kind={kind}"
            : $"{reason} | phase={phase} kind={kind} ex={exceptionType}";
}
#endif
