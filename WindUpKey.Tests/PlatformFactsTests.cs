namespace WindUpKey.Tests;

/// <summary>When the permission-based facts skip their tests (decision 38).</summary>
public sealed class PlatformFactsTests
{
    [Theory]
    [InlineData(false, 0u, true, true)] // root ignores file modes
    [InlineData(false, 0u, false, true)]
    [InlineData(false, 1000u, true, false)]
    [InlineData(false, 1u, false, false)]
    [InlineData(false, null, true, false)] // unknown: run it
    [InlineData(true, null, true, true)] // Unix-only
    [InlineData(true, null, false, false)] // every OS: runs on Windows
    public void PermissionTests_AreSkippedAsRoot_AndUnixOnlyOnesOnWindows(bool isWindows, uint? effectiveUserId, bool unixOnly, bool skipped)
    {
        var reason = PermissionFacts.SkipReason(isWindows, effectiveUserId, unixOnly);
        Assert.Equal(skipped, reason is not null);
        if (!isWindows && effectiveUserId == 0)
            Assert.Contains("root", reason);
    }

    [Fact]
    public void EffectiveUserId_IsTheProcesssEffectiveUid()
    {
        if (!OperatingSystem.IsLinux())
            return;

        // /proc/self/status: "Uid:\treal\teffective\tsaved\tfs".
        var uidLine = File.ReadLines("/proc/self/status").First(l => l.StartsWith("Uid:", StringComparison.Ordinal));
        var effective = uint.Parse(uidLine.Split('\t', StringSplitOptions.RemoveEmptyEntries)[2], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(effective, PermissionFacts.EffectiveUserId());
        Assert.Equal(effective, PermissionFacts.CurrentEffectiveUserId());
    }

    [Fact]
    public void TheAttributes_UseTheEffectiveUid()
    {
        var windows = OperatingSystem.IsWindows();
        var uid = PermissionFacts.CurrentEffectiveUserId();
        Assert.Equal(PermissionFacts.SkipReason(windows, uid, unixOnly: true), new UnixPermissionFactAttribute().Skip);
        Assert.Equal(PermissionFacts.SkipReason(windows, uid, unixOnly: false), new NotAsRootFactAttribute().Skip);
    }
}
