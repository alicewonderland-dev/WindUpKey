using System.Text.RegularExpressions;

namespace WindUpKey.Tests;

/// <summary>
/// Issue 11: Call and the quests (now the quest board) ship in Release; only the self-test and its diagnostics stay behind
/// <c>WINDUP_TESTING</c>. <c>CallTravelService</c>, <c>ConfigWindow</c> and the quest files need Dalamud, so these read
/// their source.
/// </summary>
public sealed class ReleaseGatingTests
{
    // ---------- 11-D3: a doll without Lifestream or vnavmesh ----------

    [Fact]
    public void TheDoll_IsToldWhatToInstall_AndTheGenericEcho_IsPlain()
    {
        var source = CallTravelSource;
        Assert.Contains("private const string CallErrorEcho = \"Your owner's Call ran into a problem.\";", source);
        Assert.Contains(
            "private const string NoTravelPluginsEcho =\n        \"Your owner tried to Call you. Lifestream and vnavmesh must be installed and ready to answer Calls.\";",
            source);

        // The no-travel branch shows that line, and the owner still gets the same Failed result.
        var branch = source.IndexOf("if (!hasDeferredGameGate && !IsTravelReady)", StringComparison.Ordinal);
        Assert.True(branch >= 0);
        var end = source.IndexOf("\n            return;\n", branch, StringComparison.Ordinal);
        Assert.True(end > branch);
        var block = source[branch..end];
        Assert.Contains("\"request rejected: travel plugins unavailable\",", block);
        Assert.Contains("NoTravelPluginsEcho);", block);
        Assert.Contains("CallResultStatuses.Failed, \"Lifestream and vnavmesh are required.\"", block);

        var report = ProfileSettingsTests.MethodBody(source, "    private void ReportEdgeCaseError(");
        Assert.Contains("string echo = CallErrorEcho)", report);
        Assert.Contains("PluginChat.Print(_chat, echo, PluginChat.Yellow);", report);
    }

    [Fact]
    public void ThePlugin_NoLongerSaysCallIsInTesting()
    {
        foreach (var (relative, text) in PluginFiles())
        {
            Assert.False(
                text.Contains("still in testing", StringComparison.OrdinalIgnoreCase),
                $"{relative} still says \"still in testing\"");
            Assert.False(
                text.Contains("In this Testing build", StringComparison.OrdinalIgnoreCase),
                $"{relative} still says \"In this Testing build\"");
        }
    }

    // ---------- 11-D3 and the privacy line: the owner's Call button ----------

    [Fact]
    public void TheOwnersCallButton_IsDisabledUntilTheDollCanTravel_AndSaysWhy()
    {
        var window = ConfigWindowSource;
        Assert.Contains(
            "private const string CallTravelNotReadyTooltip =\n        \"This doll can't answer Calls yet. They need Lifestream and vnavmesh, and a current version of Wind-Up Key. Press Refresh settings to check again.\";",
            window);
        Assert.Contains("private const string CallLocationTooltip = \"Sends your current location to this doll.\";", window);

        var button = ProfileSettingsTests.MethodBody(window, "    private void DrawOwnerCallButton(");
        // No snapshot (or one without data) counts as not ready, as does a 0.2.4.0 doll's TravelReady=false.
        Assert.Contains("var travelReady = snap is { HasData: true, TravelReady: true };", button);
        Assert.Contains("var enabled = online && canCall && inWorld && _relay.IsConnected && travelReady;", button);

        // One more reason, after the existing ones and in their order.
        var reasons = new[]
        {
            "if (!canCall)",
            "else if (!online)",
            "else if (!inWorld)",
            "else if (!_relay.IsConnected)",
            "else if (!travelReady)\n                ImGui.TextUnformatted(CallTravelNotReadyTooltip);",
            "else\n                ImGui.TextUnformatted(\"Call the doll to travel near your current position.\");",
        };
        var at = -1;
        foreach (var reason in reasons)
        {
            var next = button.IndexOf(reason, StringComparison.Ordinal);
            Assert.True(next > at, $"'{reason}' is missing or out of order");
            at = next;
        }

        // The location line shows whatever the state, right before the tooltip ends.
        Assert.Contains("ImGui.TextUnformatted(CallLocationTooltip);\n            ImGui.EndTooltip();", button);
    }

    // ---------- 11-D4: quests match the English names ----------

    [Fact]
    public void QuestSheetReads_ThatMatchNames_AreEnglish()
    {
        var catalog = ProfileSettingsTests.PluginSource(Path.Combine("Quest", "QuestContentCatalog.cs"));
        var reads = Regex.Matches(catalog, @"GetExcelSheet<([\w.]+)>\(([^)]*)\)").ToList();
        Assert.Equal(
            ["ContentFinderCondition", "ContentRoulette", "JournalSection", "Lumina.Excel.Sheets.Quest"],
            reads.Select(m => m.Groups[1].Value).Distinct().Order());
        Assert.All(reads, m => Assert.Equal("ClientLanguage.English", m.Groups[2].Value));

        // The board service and its game reads only look rows up by ID (display names), so they may keep the client
        // language; anything else they read is English.
        foreach (var file in new[] { "QuestBoardService.cs", "QuestGameFacts.cs" })
        {
            var source = ProfileSettingsTests.PluginSource(Path.Combine("Services", file));
            var lookups = Regex.Matches(source, @"GetExcelSheet<[\w.]+>\(([^)]*)\)(\?\.\w+)?").ToList();
            Assert.NotEmpty(lookups);
            Assert.All(lookups, m => Assert.True(
                m.Groups[1].Value == "ClientLanguage.English" || m.Groups[2].Value == "?.TryGetRow",
                $"{file}: '{m.Value}' reads names in the client language"));
        }
    }

    // ---------- 11 Part A: what WINDUP_TESTING still gates ----------

    /// <summary>The files that are self-test or Testing-only diagnostics as a whole, or in every region they gate.</summary>
    private static readonly string[] GatedFiles =
    [
        Path.Combine("Services", "InputDiagnosticLog.cs"),
        Path.Combine("Services", "RelayIdentityStore.cs"),
        Path.Combine("Services", "HousingCallLocation.cs"),
    ];

    /// <summary>The files that wire the self-test in, region by region.</summary>
    private static readonly string[] SelfTestHosts = ["Plugin.cs", Path.Combine("Services", "RelayClient.cs")];

    /// <summary>What marks a self-test region in <see cref="SelfTestHosts"/>.</summary>
    private static readonly string[] SelfTestMarkers = ["SelfTest", "_selfTest", "selftest", "ISigScanner", "InputDiagnosticLog"];

    private static readonly string[] CallAndQuestFiles =
    [
        Path.Combine("Services", "CallAckGate.cs"),
        Path.Combine("Services", "CallAskFirst.cs"),
        Path.Combine("Services", "CallCorroboration.cs"),
        Path.Combine("Services", "CallDeadlines.cs"),
        Path.Combine("Services", "CallHandoffRules.cs"),
        Path.Combine("Services", "CallLockPolicy.cs"),
        Path.Combine("Services", "CallLogRules.cs"),
        Path.Combine("Services", "CallQuietPeriod.cs"),
        Path.Combine("Services", "CallRequestRules.cs"),
        Path.Combine("Services", "CallStuckDetector.cs"),
        Path.Combine("Services", "PendingCallBook.cs"),
        Path.Combine("Services", "HousingYesnoMatcher.cs"),
        Path.Combine("Services", "CallTravelService.cs"),
        Path.Combine("Services", "CallTravelDebugLog.cs"),
        Path.Combine("Ui", "CallPromptWindow.cs"),
        Path.Combine("Quest", "QuestDifficulty.cs"),
        Path.Combine("Quest", "QuestSlot.cs"),
        Path.Combine("Quest", "QuestBoardState.cs"),
        Path.Combine("Quest", "QuestResetClock.cs"),
        Path.Combine("Quest", "StickerBoard.cs"),
        Path.Combine("Quest", "QuestRewardRules.cs"),
        Path.Combine("Quest", "QuestBoardEngine.cs"),
        Path.Combine("Quest", "QuestRollRules.cs"),
        Path.Combine("Quest", "QuestContentCatalog.cs"),
        Path.Combine("Services", "QuestGameFacts.cs"),
        Path.Combine("Services", "QuestBoardService.cs"),
        Path.Combine("Ui", "QuestBoardTab.cs"),
    ];

    [Fact]
    public void NoCallOrQuestFile_IsGated()
    {
        foreach (var relative in CallAndQuestFiles)
        {
            var text = ProfileSettingsTests.PluginSource(relative);
            Assert.False(text.StartsWith("#if", StringComparison.Ordinal), $"{relative} starts with #if");
            Assert.DoesNotMatch(@"(?m)^\s*#if\b.*\bWINDUP_TESTING\b", text);
        }
    }

    [Fact]
    public void WindupTesting_GatesOnlyTheSelfTestAndItsDiagnostics()
    {
        var stray = new List<string>();
        foreach (var (relative, text) in PluginFiles())
        {
            if (relative.StartsWith("SelfTest" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || GatedFiles.Contains(relative))
                continue;

            var lines = text.Split('\n');
            var regions = GatedRegions(lines).ToList();
            if (regions.Count == 0)
                continue;

            if (!SelfTestHosts.Contains(relative))
            {
                stray.AddRange(regions.Select(r => $"{relative}:{r.Line}"));
                continue;
            }

            // A self-test region may also touch a field another self-test region declares (e.g. _acceptedConnections).
            var markers = SelfTestMarkers.ToList();
            foreach (var (_, body) in regions.Where(r => IsSelfTest(r.Body, SelfTestMarkers)))
                markers.AddRange(Regex.Matches(body, @"(?m)^\s*private\s+[^=;(]*?\b(_\w+)\s*[;=]").Select(m => m.Groups[1].Value));

            foreach (var (line, body) in regions)
            {
                // A self-test region must not carry Call or quest code with it (e.g. CancelActiveCall next to
                // _selfTest.OnLogout); the self-test's constructor block may only read the Call service's state.
                var shipped = body.Contains("new SelfTestController(", StringComparison.Ordinal)
                    ? SelfTestCallReads.Aggregate(body, (b, read) => b.Replace(read, string.Empty, StringComparison.Ordinal))
                    : body;
                if (CallAndQuestCode.Match(shipped) is { Success: true } call)
                    stray.Add($"{relative}:{line} ({call.Value})");
                else if (!IsSelfTest(body, markers) && !IsInputDiagnosticsSwitch(lines, line))
                    stray.Add($"{relative}:{line}");
            }
        }

        Assert.Empty(stray);
    }

    /// <summary>Call and quest code that must never sit inside a <c>WINDUP_TESTING</c> region.</summary>
    private static readonly Regex CallAndQuestCode = new(
        @"\b(CancelActiveCall|TickCallTravel|AttachCallTravel|_callTravel|_callPrompt|CallPromptWindow|CallTravelService"
        + @"|SendCall\w*|RequestDeclineActiveCall|\w*DebugCallPoint|I?PartyList|_pendingCalls|HandleInboundCall|HandleCall\w*"
        + @"|MessageTypes\.Call\w*|QuestBoardService|QuestBoardTab|quests|AddQuestWind|stopcall)\b");

    /// <summary>What the self-test's constructor lambdas read from the Call service (Plugin.cs).</summary>
    private static readonly string[] SelfTestCallReads =
    [
        "_callTravel.IsTravelReady",
        "_callTravel.HasActiveCall",
        "_callTravel.IsLandingGraceActive",
    ];

    /// <summary>The Call and quest statements that Part A split out of Plugin.cs's Testing regions.</summary>
    private static readonly string[] PluginCallAndQuestStatements =
    [
        "[PluginService] internal static IPartyList PartyList { get; private set; } = null!;",
        "private readonly CallPromptWindow _callPrompt;",
        "private readonly CallTravelService _callTravel;",
        "var quests = new QuestBoardService(",
        "_callPrompt = new CallPromptWindow();",
        "_callTravel = new CallTravelService(",
        "_relay.AttachCallTravel(_callTravel);",
        "quests,",
        "_windowSystem.AddWindow(_callPrompt);",
        "_sources.Add(quests);",
        "+ \"\\n/windup stopcall declines an active owner call (not in Hardcore).\"",
        "_callTravel.Dispose();",
        "_relay.TickCallTravel();",
        "_relay.CancelActiveCall();",
        "if (string.Equals(verb, \"stopcall\", StringComparison.OrdinalIgnoreCase))",
        "if (!_relay.RequestDeclineActiveCall())",
    ];

    [Fact]
    public void PluginsCallAndQuestStatements_AreOutsideEveryTestingRegion()
    {
        var lines = ProfileSettingsTests.PluginSource("Plugin.cs").Split('\n');
        var spans = GatedSpans(lines).ToList();
        var problems = new List<string>();
        foreach (var statement in PluginCallAndQuestStatements)
        {
            var at = Enumerable.Range(0, lines.Length).Where(i => lines[i].Trim() == statement).ToList();
            if (at.Count == 0)
                problems.Add($"not found: {statement}");
            foreach (var i in at.Where(i => spans.Any(s => i > s.Start && i < s.End)))
                problems.Add($"gated at line {i + 1}: {statement}");
        }

        // The logout and the safeword both cancel the Call.
        Assert.Equal(2, lines.Count(l => l.Trim() == "_relay.CancelActiveCall();"));
        Assert.Empty(problems);
    }

    /// <summary>Each <c>#if … WINDUP_TESTING</c> to its matching <c>#endif</c> (0-based line indexes), branches included.</summary>
    private static IEnumerable<(int Start, int End)> GatedSpans(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (!Regex.IsMatch(lines[i], @"^\s*#if\b.*\bWINDUP_TESTING\b"))
                continue;

            var depth = 0;
            var end = i + 1;
            for (; end < lines.Length; end++)
            {
                var directive = lines[end].TrimStart();
                if (directive.StartsWith("#if", StringComparison.Ordinal))
                    depth++;
                else if (directive.StartsWith("#endif", StringComparison.Ordinal) && depth-- == 0)
                    break;
            }

            yield return (i, end);
        }
    }

    private static bool IsSelfTest(string body, IEnumerable<string> markers) =>
        markers.Any(m => body.Contains(m, StringComparison.Ordinal));

    /// <summary>C2: <c>Plugin.cs</c> turns the diagnostics log on in Testing builds with an <c>enabled:</c> argument.</summary>
    private static bool IsInputDiagnosticsSwitch(string[] lines, int line)
    {
        var before = string.Join('\n', lines[..(line - 1)]);
        var call = before.LastIndexOf("new ", StringComparison.Ordinal);
        return call >= 0
            && before[call..].StartsWith("new InputDiagnosticLog(", StringComparison.Ordinal)
            && lines[line].Trim() == "enabled: true";
    }

    /// <summary>Each <c>#if … WINDUP_TESTING</c> (1-based line) and its text up to its <c>#else</c> or <c>#endif</c>.</summary>
    private static IEnumerable<(int Line, string Body)> GatedRegions(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (!Regex.IsMatch(lines[i], @"^\s*#if\b.*\bWINDUP_TESTING\b"))
                continue;

            var depth = 0;
            var end = i + 1;
            for (; end < lines.Length; end++)
            {
                var directive = lines[end].TrimStart();
                if (directive.StartsWith("#if", StringComparison.Ordinal))
                    depth++;
                else if (directive.StartsWith("#endif", StringComparison.Ordinal) && depth-- == 0)
                    break;
                else if (depth == 0 && (directive.StartsWith("#else", StringComparison.Ordinal) || directive.StartsWith("#elif", StringComparison.Ordinal)))
                    break;
            }

            yield return (i + 1, string.Join('\n', lines[(i + 1)..Math.Min(end, lines.Length)]));
        }
    }

    /// <summary>
    /// Every plugin source file (relative path, LF text), without build output. <c>RelaySecrets.cs</c> holds the relay
    /// token and is never read.
    /// </summary>
    private static IEnumerable<(string Relative, string Text)> PluginFiles()
    {
        var root = Path.GetFullPath(ProfileSettingsTests.PluginPath(""));
        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, path);
            var top = relative.Split(Path.DirectorySeparatorChar)[0];
            if (top is "bin" or "obj" || relative == "RelaySecrets.cs")
                continue;
            yield return (relative, File.ReadAllText(path).Replace("\r\n", "\n"));
        }
    }

    private static string CallTravelSource => ProfileSettingsTests.PluginSource(Path.Combine("Services", "CallTravelService.cs"));

    private static string ConfigWindowSource => ProfileSettingsTests.PluginSource(Path.Combine("Ui", "ConfigWindow.cs"));
}
