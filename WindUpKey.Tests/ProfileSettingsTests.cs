using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using WindUpKey.Services;
using WindUpKey.Ui;

namespace WindUpKey.Tests;

/// <summary>
/// Issue 06 Part A, the settings: the per-character lock emote and its migration (06-A1), the safeword text (06-A4) and
/// a wind never shortening the timer (06-A8). <c>Configuration</c> and <c>ConfigWindow</c> need Dalamud, so the checks on
/// them read their source.
/// </summary>
public sealed class ProfileSettingsTests
{
    // ---------- 06-A1: migration ----------

    [Fact]
    public void Migration_FillsEveryProfileWithoutItsOwnLockEmote_PendingToo()
    {
        var profiles = new Dictionary<string, CharacterProfile>
        {
            ["004000174AA8BCC2"] = new(),
            ["pending"] = new(),
        };

        ProfileMigration.FillLockEmote(profiles.Values, globalAutoGroundSit: false, globalLockEmoteId: 141);

        foreach (var profile in profiles.Values)
        {
            Assert.False(profile.AutoGroundSit);
            Assert.Equal((ushort)141, profile.LockEmoteId);
        }
    }

    [Fact]
    public void Migration_NeverOverwritesAValueAlreadySet()
    {
        var own = new CharacterProfile { AutoGroundSit = true, LockEmoteId = 88 };
        var onlyEmote = new CharacterProfile { LockEmoteId = 13 };
        var onlySit = new CharacterProfile { AutoGroundSit = false };

        ProfileMigration.FillLockEmote([own, onlyEmote, onlySit, null], globalAutoGroundSit: false, globalLockEmoteId: 141);

        Assert.True(own.AutoGroundSit);
        Assert.Equal((ushort)88, own.LockEmoteId);
        Assert.False(onlyEmote.AutoGroundSit);
        Assert.Equal((ushort)13, onlyEmote.LockEmoteId);
        Assert.False(onlySit.AutoGroundSit);
        Assert.Equal((ushort)141, onlySit.LockEmoteId);
    }

    [Fact]
    public void Migration_RunTwice_ChangesNothingTheSecondTime()
    {
        // Release <-> Testing: only what an older build dropped is refilled; a migrated profile stays as it is even when
        // the globals (the active character's mirror) have changed since.
        var profile = new CharacterProfile();
        ProfileMigration.FillLockEmote([profile], globalAutoGroundSit: false, globalLockEmoteId: 141);

        ProfileMigration.FillLockEmote([profile], globalAutoGroundSit: true, globalLockEmoteId: 52);
        Assert.False(profile.AutoGroundSit);
        Assert.Equal((ushort)141, profile.LockEmoteId);
    }

    [Fact]
    public void Migration_IsCalledFromMigrate_ForEveryProfile_AndANewCharacterGetsTheDefaults()
    {
        var migrate = MethodBody(ConfigurationSource, "public void Migrate()");
        Assert.Contains("ProfileMigration.FillLockEmote(Profiles.Values, AutoGroundSit, LockEmoteId);", migrate);
        Assert.DoesNotContain("Version <", migrate);

        var activate = MethodBody(ConfigurationSource, "public bool ActivateCharacter(ulong contentId)");
        Assert.Contains("AutoGroundSit = ProfileMigration.NewProfileAutoGroundSit,", activate);
        Assert.Contains("LockEmoteId = ProfileMigration.NewProfileLockEmoteId,", activate);
        Assert.True(ProfileMigration.NewProfileAutoGroundSit);
        Assert.Equal((ushort)52, ProfileMigration.NewProfileLockEmoteId);
        Assert.Matches(@"public const int CurrentVersion = 14;", ConfigurationSource);
        // 14: every profile gets its wind layers from its expiry, gated on the missing layers, never on Version.
        Assert.Contains("ProfileMigration.FillWindLayers(Profiles.Values, migratedAt);", migrate);
        // 13: every profile's quest board is normalised, never gated on Version either.
        Assert.Contains("profile.QuestBoard.Normalize();", migrate);
    }

    // ---------- 06-A1: the four places a per-character field is written ----------

    [Fact]
    public void EveryProfileField_IsCapturedAndApplied_AndHasItsActiveMirror()
    {
        // From the source (the quest fields included); reflection checks the parse found them all.
        var names = Regex.Matches(ProfileSource, @"public\s+[\w<>?,\s]+?\s+(\w+)\s*\{\s*get;\s*set;\s*\}")
            .Select(m => m.Groups[1].Value)
            .ToList();
        var reflected = typeof(CharacterProfile).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name);
        Assert.Subset(names.ToHashSet(), reflected.ToHashSet());
        Assert.Contains("AutoGroundSit", names);
        Assert.Contains("LockEmoteId", names);
        // 06-B1: the safeword pause follows the same pattern.
        Assert.Contains("SafewordPausedUntilUtc", names);
        Assert.Contains("QuestBoard", names);
        Assert.Contains("Achievements", names);

        var capture = MethodBody(ConfigurationSource, "public CharacterProfile CaptureActiveAsProfile()");
        var apply = MethodBody(ConfigurationSource, "public void ApplyProfile(CharacterProfile profile)");
        var missing = new List<string>();
        foreach (var name in names)
        {
            if (!Regex.IsMatch(capture, $@"^\s*{name}\s*=", RegexOptions.Multiline))
                missing.Add($"{name}: not in CaptureActiveAsProfile");
            if (!Regex.IsMatch(apply, $@"^\s*{name}\s*=.*\bprofile\.{name}\b", RegexOptions.Multiline))
                missing.Add($"{name}: not in ApplyProfile");
            if (!Regex.IsMatch(ConfigurationSource, $@"public\s+[\w<>?,\s]+?\s+{name}\s*\{{\s*get;\s*set;\s*\}}"))
                missing.Add($"{name}: no Configuration property");
        }

        Assert.Empty(missing);
    }

    // ---------- 06-A1: the lock emote combo ----------

    private static readonly IReadOnlyList<(ushort Id, string Name)> Unlocked =
    [
        (50, "Sit"),
        (52, "Sit on Ground"),
        (88, "Sleep"),
    ];

    [Fact]
    public void LockEmoteDisplay_ShowsAnUnlockedEmoteByItsName()
    {
        var view = LockEmoteDisplay.Resolve(88, Unlocked, "Sleep");

        Assert.Equal(new LockEmoteView(88, "Sleep", Unlocked: true), view);
    }

    [Fact]
    public void LockEmoteDisplay_KeepsAnEmoteThisCharacterHasNotUnlocked_WithTheNote()
    {
        // An alt opening the config: the main's emote stays selected, and nothing asks for Ground Sit to be stored.
        var view = LockEmoteDisplay.Resolve(141, Unlocked, "Pray");

        Assert.Equal((ushort)141, view.SelectedId);
        Assert.False(view.Unlocked);
        Assert.Equal("Pray (not available on this character; Ground Sit is used)", view.Preview);
        Assert.Equal("Emote #141" + LockEmoteDisplay.GroundSitFallbackNote, LockEmoteDisplay.Resolve(141, Unlocked, null).Preview);
    }

    [Fact]
    public void LockEmoteDisplay_ReadsZeroAsGroundSit_AndGroundSitNeverGetsTheNote()
    {
        Assert.Equal(new LockEmoteView(52, "Sit on Ground", Unlocked: true), LockEmoteDisplay.Resolve(0, Unlocked, null));
        Assert.Equal(new LockEmoteView(52, "Sit on Ground", Unlocked: true), LockEmoteDisplay.Resolve(52, [], null));
    }

    [Fact]
    public void GroundSit_IsOneNamedConstant()
    {
        // 06-A13: Sit on Ground (52) is defined once, and the default, the fallback and "0 is Ground Sit" all use it.
        Assert.Equal((ushort)52, CharacterProfile.GroundSitEmoteId);
        Assert.Equal(CharacterProfile.GroundSitEmoteId, ProfileMigration.NewProfileLockEmoteId);
        Assert.Equal(CharacterProfile.GroundSitEmoteId, LockEmoteDisplay.Resolve(0, [], null).SelectedId);

        var root = PluginPath(".");
        var definitions = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Sum(f => Regex.Matches(File.ReadAllText(f), @"GroundSitEmoteId\s*=(?!=)|const\s+ushort\s+\w+\s*=\s*52\b").Count);
        Assert.Equal(1, definitions);
        Assert.Contains("public const ushort GroundSitEmoteId = 52;", ProfileSource);
        Assert.Contains("public ushort EffectiveLockEmoteId => LockEmoteId == 0 ? CharacterProfile.GroundSitEmoteId : LockEmoteId;", ConfigurationSource);
        var runner = PluginSource(Path.Combine("Services", "GameCommandRunner.cs"));
        Assert.DoesNotMatch(@"\b52\b", runner);
        Assert.DoesNotMatch(@"\b52\b", PluginSource(Path.Combine("Ui", "LockEmoteDisplay.cs")));
        Assert.DoesNotMatch(@"\b52\b", PluginSource("ProfileMigration.cs"));
    }

    [Fact]
    public void LockEmoteDisplay_NeverWrites_TheComboStoresOnlyAPickedEmote()
    {
        // The view is data only (no member that could ask for a write)...
        Assert.Equal(
            new[] { "Preview", "SelectedId", "Unlocked" },
            typeof(LockEmoteView).GetProperties().Select(p => p.Name).Order());

        // ...and the doll's combo writes LockEmoteId only in its selection callback, never while drawing.
        var combo = MethodBody(ConfigWindowSource, "private void DrawLockEmoteCombo()");
        Assert.Contains("LockEmoteDisplay.Resolve(", combo);
        var writes = Regex.Matches(combo, @"_config\.LockEmoteId\s*=");
        Assert.Single(writes);
        var callback = combo.IndexOf("id =>", StringComparison.Ordinal);
        Assert.True(callback >= 0 && writes[0].Index > callback, "LockEmoteId is written outside the selection callback");
        Assert.True(Regex.Matches(combo, @"_config\.Save\(\)").Count == 1, "the combo saves outside the selection callback");
    }

    [Fact]
    public void LockEmoteCombo_LooksUpTheEmoteNameOnlyWhenTheStoredEmoteChanges()
    {
        // 06-A12: not a sheet lookup every frame; cached per emote id.
        var combo = MethodBody(ConfigWindowSource, "private void DrawLockEmoteCombo()");
        Assert.Single(Regex.Matches(combo, @"GetEmoteName\("));
        Assert.Contains(
            "        if (_lockEmoteNameId != storedId)\n        {\n            _lockEmoteNameId = storedId;\n            _lockEmoteName = _commands.GetEmoteName(storedId);\n        }",
            combo);
        Assert.Contains("LockEmoteDisplay.Resolve(storedId, emotes, _lockEmoteName);", combo);
    }

    // ---------- 06-A4: the safeword text ----------

    [Fact]
    public void Safeword_IsTrimmedWhenStored()
    {
        Assert.Equal("pineapple", SafewordText.Store("  pineapple \t", "old"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Safeword_AnEmptyEntry_KeepsTheOldOne(string? entered)
    {
        Assert.Equal("old", SafewordText.Store(entered, "old"));
        Assert.Equal("old", SafewordText.Store(entered, " old "));
        Assert.Equal(SafewordText.Default, SafewordText.Store(entered, "  "));
    }

    [Fact]
    public void Safeword_IsComparedTrimmed_AndStillCaseSensitive()
    {
        Assert.True(SafewordText.Matches(" pineapple ", "pineapple"));
        Assert.True(SafewordText.Matches("pineapple", " pineapple  "));
        Assert.False(SafewordText.Matches("Pineapple", "pineapple"));
        Assert.False(SafewordText.Matches("pine apple", "pineapple"));
        Assert.False(SafewordText.Matches("  ", "  "));
        Assert.True(SafewordText.Matches("safeword", ""));
    }

    [Fact]
    public void Safeword_TheCommandComparesWithTheRule()
    {
        var plugin = PluginSource("Plugin.cs");
        Assert.Contains("if (!SafewordText.Matches(rest, Configuration.Safeword))", plugin);
        Assert.DoesNotContain("string.Equals(rest, Configuration.Safeword", plugin);
    }

    [Theory]
    // Max wind hours: on a +/- step at once, and when a typed edit ends.
    [InlineData("    private void DrawGeneralTab()", "ImGui.InputInt(\"Max wind hours\"", "ImGui.Checkbox(\"Play emote when unwound\"",
        "                if (!ImGui.IsItemActive())\n                    _config.Save();")]
    [InlineData("    private void DrawGeneralTab()", "ImGui.InputInt(\"Max wind hours\"", "ImGui.Checkbox(\"Play emote when unwound\"",
        "            if (ImGui.IsItemDeactivatedAfterEdit())\n                _config.Save();")]
    // The safeword checkbox, at once.
    [InlineData("    private void DrawSafewordTab()", "ImGui.Checkbox(\"Enable safeword\"", "ImGui.InputText(\"Safeword",
        "                _config.SafewordEnabled = safewordOn;\n                _config.Save();")]
    // The safeword text, stored (trimmed) when the edit ends.
    [InlineData("    private void DrawSafewordTab()", "ImGui.InputText(\"Safeword", "ImGui.SliderInt(\"Safeword hours\"",
        "            if (ImGui.IsItemDeactivatedAfterEdit())\n            {\n                _config.Safeword = SafewordText.Store(_safewordDraft, _config.Safeword);\n                _config.Save();")]
    // The safeword hours, when the slider is let go.
    [InlineData("    private void DrawSafewordTab()", "ImGui.SliderInt(\"Safeword hours\"", "ImGui.Spacing();",
        "            if (ImGui.IsItemDeactivatedAfterEdit())\n                _config.Save();")]
    public void Settings_TheA4FieldsSaveOnCommit(string member, string widget, string nextWidget, string save)
    {
        // 06-A4 / 06-A12: each is saved as it is committed, right after its own widget, so a crash or reload can't lose it.
        var body = MethodBody(ConfigWindowSource, member);
        var start = body.IndexOf(widget, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{widget}' not found");
        var end = body.IndexOf(nextWidget, start + widget.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"'{nextWidget}' not found after '{widget}'");
        Assert.Contains(save, body[start..end]);
    }

    // ---------- 06-A8: a wind never shortens the timer ----------

    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Wind_UnderTheCap_AddsTheFullAmount()
    {
        var result = WindMath.Add(Now, Now.AddHours(10), TimeSpan.FromHours(6), maxWindHours: 72);

        Assert.Equal(Now.AddHours(16), result.Expiry);
        Assert.Equal(TimeSpan.FromHours(6), result.Added);
    }

    [Fact]
    public void Wind_OverTheCap_StopsAtTheCap()
    {
        var result = WindMath.Add(Now, Now.AddHours(70), TimeSpan.FromHours(6), maxWindHours: 72);

        Assert.Equal(Now.AddHours(72), result.Expiry);
        Assert.Equal(TimeSpan.FromHours(2), result.Added);
    }

    [Fact]
    public void Wind_WhenTheTimerIsAlreadyPastTheCap_KeepsIt()
    {
        // After a quest wind above the max, or an owner lowering max hours: the wind no longer clamps it down.
        var result = WindMath.Add(Now, Now.AddHours(100), TimeSpan.FromHours(1), maxWindHours: 72);

        Assert.Equal(Now.AddHours(100), result.Expiry);
        Assert.Equal(TimeSpan.Zero, result.Added);
    }

    [Fact]
    public void Wind_OnAnEmptyOrExpiredTimer_StartsFromNow()
    {
        Assert.Equal(Now.AddHours(1), WindMath.Add(Now, null, TimeSpan.FromHours(1), 72).Expiry);
        Assert.Equal(Now.AddHours(1), WindMath.Add(Now, Now.AddHours(-5), TimeSpan.FromHours(1), 72).Expiry);
    }

    [Fact]
    public void Wind_WithoutACap_AddsEverything_AndTheCapIsNeverBelowItsMinimum()
    {
        Assert.Equal(Now.AddHours(300), WindMath.Add(Now, Now.AddHours(100), TimeSpan.FromHours(200), null).Expiry);
        Assert.Equal(Now.AddHours(WindMath.MinCapHours), WindMath.Add(Now, null, TimeSpan.FromHours(5), 0).Expiry);
    }

    [Fact]
    public void Wind_NeverReducesTheExpiry_ForAnyCapAndAmount()
    {
        foreach (var currentHours in new double[] { -3, 0, 0.5, 10, 71.9, 72, 80, 500 })
        foreach (var amountHours in new double[] { 0.01, 1, 24, 168 })
        foreach (var cap in new double?[] { null, 0, 1, 24, 72, 168 })
        {
            var current = Now.AddHours(currentHours);
            var floor = current > Now ? current : Now;
            var result = WindMath.Add(Now, current, TimeSpan.FromHours(amountHours), cap);
            Assert.True(result.Expiry >= floor, $"current {currentHours}h + {amountHours}h, cap {cap}: {result.Expiry}");
            Assert.True(result.Added >= TimeSpan.Zero);
            Assert.Equal(result.Expiry - floor, result.Added);
        }
    }

    [Fact]
    public void Wind_TheTimerUsesTheLayerRules()
    {
        // Spare springs: every wind goes through WindLayers, each to its own layer, and the stored expiry is derived.
        var timer = PluginSource(Path.Combine("Services", "WindTimerService.cs"));
        Assert.Contains("before.AddPlayer(hours, _config.MaxWindHours, now, _config.BrokenRecoveryMinutes)",
            MethodBody(timer, "    public PlayerWindResult AddPlayerWind("));
        Assert.Contains("Save(Layers.AddQuest(hours, _config.MaxWindHours, now), now);", MethodBody(timer, "    public void AddQuestWind("));
        Assert.Contains("before.AddNormal(bonus.TotalHours, _config.MaxWindHours, now)", MethodBody(timer, "    public TimeSpan AddCommendationWind("));
        Assert.Contains("WriteLayers(Layers.Unwind(now), now);", MethodBody(timer, "    public void ClearWind("));
        Assert.Contains("_config.ExpiryUtc = at.StopsAt(now);", MethodBody(timer, "    private void WriteLayers("));
        Assert.DoesNotContain("WindMath.Add(", timer);
    }

    // ---------- source helpers ----------

    internal static string PluginSource(string relative, [CallerFilePath] string here = "") =>
        File.ReadAllText(PluginPath(relative, here)).Replace("\r\n", "\n");

    internal static string PluginPath(string relative, [CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "..", "WindUpKey", relative);

    private static string ConfigurationSource => PluginSource("Configuration.cs");

    private static string ProfileSource => PluginSource("CharacterProfile.cs");

    private static string ConfigWindowSource => PluginSource(Path.Combine("Ui", "ConfigWindow.cs"));

    /// <summary>The text of a member from its signature to its closing brace at the same (four-space) indent.</summary>
    internal static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' not found");
        var end = source.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        Assert.True(end > start, $"end of '{signature}' not found");
        return source[start..end];
    }
}
