using System.Diagnostics;
using System.Globalization;
using WindUpKey.Protocol;
using WindUpKey.Services;

namespace WindUpKey.Tests;

/// <summary>
/// What the client accepts from partners and relay sync (02-D3, 02-D6, 02-D7, 02-D10, 02-D18, 02-D21 to 02-D23, 02-D27,
/// 02-D29), and what removing a partner clears here (02-D30).
/// </summary>
public sealed class RelaySyncRulesTests
{
    private const string Me = "MEMEME11";
    private const string Partner = "PARTNR22";

    // ---------- 02-D3 and 02-D18: presence replies ----------

    // ---------- 08-T6 (D10): the re-query after a safeword refusal keeps the "paused" message once ----------

    [Fact]
    public void OwnerSettingsResult_ClearsTheError_UnlessTheReQueryKeepsIt()
    {
        Assert.Null(RelaySyncRules.OwnerSettingsErrorAfterResult("That doll is offline.", keepErrorOnNextResult: false));
        Assert.Null(RelaySyncRules.OwnerSettingsErrorAfterResult(null, keepErrorOnNextResult: false));
        const string paused = "That doll has paused remote control for now.";
        Assert.Equal(paused, RelaySyncRules.OwnerSettingsErrorAfterResult(paused, keepErrorOnNextResult: true));
        Assert.Null(RelaySyncRules.OwnerSettingsErrorAfterResult(null, keepErrorOnNextResult: true));
    }

    [Theory]
    [InlineData(true, "req1", "req1", true)]
    [InlineData(true, "req1", "req2", false)]
    [InlineData(true, "req1", null, false)]
    [InlineData(true, null, "req1", false)]
    [InlineData(true, null, null, false)]
    [InlineData(true, "", "", false)]
    [InlineData(false, "req1", "req1", false)]
    public void OwnerSettingsResult_KeepsTheError_OnlyForTheReQuerysOwnRequestId(
        bool keepErrorOnNextResult, string? reQueryRequestId, string? resultRequestId, bool expected)
    {
        // 08-D17: an earlier query's result (another RequestId), or one before the re-query was sent, clears it.
        Assert.Equal(expected, RelaySyncRules.IsOwnerSettingsReQueryResult(keepErrorOnNextResult, reQueryRequestId, resultRequestId));
    }

    // ---------- 08-D15: a presence answer leaves stillPaired unset until the register is accepted ----------

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, null)]
    [InlineData(false, false, null)]
    public void PresenceAnswer_LeavesStillPairedUnset_UntilTheRegisterIsAccepted(bool stillPaired, bool registerAccepted, bool? expected)
    {
        // Before this connection's pairSync the local list may be empty (a second PC, a fresh install): a false from it
        // would take a real partner off the asker's list (only there: never on the relay, 08-D19).
        Assert.Equal(expected, RelaySyncRules.PresenceStillPairedAnswer(stillPaired, registerAccepted));
    }

    [Fact]
    public void Presence_FromAPartner_IsAnsweredStillPaired()
    {
        Assert.True(RelaySyncRules.TryAnswerPresence(Partner, Me, debugEnabled: false, paired: true, out var stillPaired));
        Assert.True(stillPaired);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Presence_FromAnUnpairedSender_IsAnsweredNotStillPaired(bool debugEnabled)
    {
        // 02-D18: the sender learns it's no longer paired, so a half-unpair heals on its side.
        Assert.True(RelaySyncRules.TryAnswerPresence(Partner, Me, debugEnabled, paired: false, out var stillPaired));
        Assert.False(stillPaired);
    }

    [Fact]
    public void Presence_FromTheSelfKey_IsAnsweredOnlyWhenPairedOrInDebug()
    {
        Assert.True(RelaySyncRules.TryAnswerPresence(Me, Me, debugEnabled: true, paired: false, out var stillPaired));
        Assert.False(stillPaired);
        Assert.True(RelaySyncRules.TryAnswerPresence(Me, Me, debugEnabled: false, paired: true, out stillPaired));
        Assert.True(stillPaired);
        Assert.False(RelaySyncRules.TryAnswerPresence(Me, Me, debugEnabled: false, paired: false, out _));
    }

    [Theory]
    [InlineData("", Me)]
    [InlineData("SHORT", Me)]
    [InlineData(Partner, "")]
    public void Presence_WithAnInvalidKey_IsNotAnswered(string from, string local)
    {
        Assert.False(RelaySyncRules.TryAnswerPresence(from, local, debugEnabled: true, paired: true, out _));
    }

    // ---------- 02-D6: remote stamps ahead of the local clock ----------

    [Fact]
    public void RemoteStamp_WithinFiveMinutesAhead_IsRead()
    {
        var now = DateTimeOffset.UtcNow;
        var ahead = now.AddMinutes(4).AddSeconds(59);
        Assert.True(RelaySyncRules.TryReadRemoteStamp(ahead.ToString("O"), now, out var stamp));
        Assert.Equal(ahead, stamp);
        Assert.True(RelaySyncRules.TryReadRemoteStamp(now.AddDays(-3).ToString("O"), now, out _));
    }

    [Fact]
    public void RemoteStamp_MoreThanFiveMinutesAhead_IsTreatedAsAbsent()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.False(RelaySyncRules.TryReadRemoteStamp(now.AddMinutes(5).AddSeconds(1).ToString("O"), now, out _));
        Assert.False(RelaySyncRules.TryReadRemoteStamp(now.AddYears(30).ToString("O"), now, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a date")]
    public void RemoteStamp_Unreadable_IsTreatedAsAbsent(string? raw)
    {
        Assert.False(RelaySyncRules.TryReadRemoteStamp(raw, DateTimeOffset.UtcNow, out _));
    }

    [Fact]
    public void RemoteStamp_UsesTheSharedSkew()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), SyncStamp.MaxFutureSkew);
    }

    // ---------- 02-D6: culture-invariant parsing ----------

    [Theory]
    [InlineData("th-TH")]
    [InlineData("ar-SA")]
    [InlineData("fa-IR")]
    [InlineData("de-DE")]
    [InlineData("en-GB")]
    public void Stamps_AreParsedTheSameInEveryCulture(string cultureName)
    {
        var stamp = new DateTimeOffset(2026, 10, 12, 18, 30, 15, TimeSpan.Zero).AddTicks(1234567);
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(cultureName);
            Assert.True(SyncStamp.TryParse(stamp.ToString("O", CultureInfo.InvariantCulture), out var parsed));
            Assert.Equal(stamp, parsed);
            Assert.True(SyncStamp.TryParse("2026-10-12T18:30:15+02:00", out var offset));
            Assert.Equal(new DateTimeOffset(2026, 10, 12, 16, 30, 15, TimeSpan.Zero), offset.ToUniversalTime());

            // Older non-round-trip values are read month-first (invariant), not the way this culture writes dates.
            Assert.True(SyncStamp.TryParse("10/12/2026 18:30:15 +00:00", out var older));
            Assert.Equal(new DateTimeOffset(2026, 10, 12, 18, 30, 15, TimeSpan.Zero), older);

            // A stamp written now isn't read as far in the future (or past) under another calendar.
            var now = DateTimeOffset.UtcNow;
            Assert.True(RelaySyncRules.TryReadRemoteStamp(now.ToString("O"), now, out var read));
            Assert.Equal(now, read);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void OlderStoredValues_FallBackToInvariantParsing_AsUtc()
    {
        // Not round-trip values: these take the fallback.
        Assert.True(SyncStamp.TryParse("2026-10-12 18:30:15", out var parsed));
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 18, 30, 15, TimeSpan.Zero), parsed);
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
        Assert.True(SyncStamp.TryParse("10/12/2026 18:30:15 +00:00", out var invariant));
        Assert.Equal(parsed, invariant);
    }

    [Fact]
    public void OffsetlessRoundTripValues_AreUtc()
    {
        // 02-D15 and 02-D25: the round-trip path (not the fallback), under this machine's own zone.
        Assert.True(SyncStamp.TryParse("2026-10-12T18:30:15.1234567", out var parsed));
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 18, 30, 15, TimeSpan.Zero).AddTicks(1234567), parsed);
    }

    // 02-D29: each zone is its own test, skipped (with the reason) where the host doesn't have it.
    [TimeZoneFact("Asia/Tokyo")]
    public Task OffsetlessRoundTripValues_AreUtc_InTokyo() => AssertOffsetlessRoundTripIsUtcAsync("Asia/Tokyo");

    [TimeZoneFact("America/Sao_Paulo")]
    public Task OffsetlessRoundTripValues_AreUtc_InSaoPaulo() => AssertOffsetlessRoundTripIsUtcAsync("America/Sao_Paulo");

    /// <summary>
    /// Parses in a separate process, so the zone (TZ) applies from its start and nothing else in this run sees it.
    /// Both of its output streams are read at once, so neither can fill its pipe and stall it.
    /// </summary>
    private static async Task AssertOffsetlessRoundTripIsUtcAsync(string zone)
    {
        const string raw = "2026-10-12T18:30:15.1234567";
        var expected = new DateTimeOffset(2026, 10, 12, 18, 30, 15, TimeSpan.Zero).AddTicks(1234567);
        var worker = typeof(Program).Assembly.Location;
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } path ? path : "dotnet";
        var start = new ProcessStartInfo(host, [worker, Program.StampParseCommand, raw])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["TZ"] = zone;

        using var proc = Process.Start(start)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
        {
            try
            {
                await proc.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                proc.Kill(entireProcessTree: true);
                Assert.Fail($"TZ={zone}: the stamp parser didn't exit");
            }
        }

        var output = await stdout;
        var errors = await stderr;
        Assert.True(proc.ExitCode == 0, output + errors);

        var fields = output.Trim().Split(' ');
        Assert.True(long.Parse(fields[0], CultureInfo.InvariantCulture) != 0, $"TZ={zone} didn't take effect");
        Assert.Equal("ok", fields[1]);
        Assert.Equal(0L, long.Parse(fields[2], CultureInfo.InvariantCulture));
        Assert.Equal(expected.UtcTicks, long.Parse(fields[3], CultureInfo.InvariantCulture));
    }

    [Fact]
    public void TimeZoneTests_AreSkippedWithAReason_OnWindowsOrWithoutTheZone()
    {
        Assert.Null(TimeZoneFacts.SkipReason(isWindows: false, "Asia/Tokyo", _ => true));
        Assert.Contains("Nowhere/Atlantis", TimeZoneFacts.SkipReason(isWindows: false, "Nowhere/Atlantis", _ => false));
        Assert.Contains("TZ", TimeZoneFacts.SkipReason(isWindows: true, "Asia/Tokyo", _ => true));

        Assert.False(TimeZoneFacts.IsAvailable("Nowhere/Atlantis"));
        Assert.Equal(
            TimeZoneFacts.SkipReason(OperatingSystem.IsWindows(), "Nowhere/Atlantis", TimeZoneFacts.IsAvailable),
            new TimeZoneFactAttribute("Nowhere/Atlantis").Skip);
        Assert.NotNull(new TimeZoneFactAttribute("Nowhere/Atlantis").Skip);
    }

    [Fact]
    public void IsTooFarAhead_IsStrictlyMoreThanTheSkew()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.False(SyncStamp.IsTooFarAhead(now + SyncStamp.MaxFutureSkew, now));
        Assert.True(SyncStamp.IsTooFarAhead(now + SyncStamp.MaxFutureSkew + TimeSpan.FromTicks(1), now));
        Assert.False(SyncStamp.IsTooFarAhead(now.AddYears(-1), now));
    }

    // ---------- 02-D7: sync changes told in chat ----------

    [Fact]
    public void BeforeInitialSetup_IsQuiet_AfterItIsNot()
    {
        // 02-D21: gated on the profile's initial setup, not on its partner count.
        Assert.False(RelaySyncRules.AnnouncesSyncChanges(hasCompletedInitialSetup: false));
        Assert.True(RelaySyncRules.AnnouncesSyncChanges(hasCompletedInitialSetup: true));
    }

    [Fact]
    public void APartnerNewToThisPc_IsAnnounced()
    {
        Assert.Equal([PairSyncChange.PartnerAdded], RelaySyncRules.Changes(true, false, false, false, false));
        Assert.Empty(RelaySyncRules.Changes(false, false, false, false, false));
    }

    [Fact]
    public void OwnerAndCall_TurnedOn_AreAnnounced_OncePerChange()
    {
        Assert.Equal([PairSyncChange.MadeOwner], RelaySyncRules.Changes(false, false, false, true, false));
        Assert.Equal([PairSyncChange.AllowedCall], RelaySyncRules.Changes(false, true, false, true, true));
        Assert.Equal(
            [PairSyncChange.PartnerAdded, PairSyncChange.MadeOwner, PairSyncChange.AllowedCall],
            RelaySyncRules.Changes(true, false, false, true, true));
    }

    [Fact]
    public void FlagsAlreadyOn_OrTurnedOff_AreNotAnnounced()
    {
        Assert.Empty(RelaySyncRules.Changes(false, true, true, true, true));
        Assert.Empty(RelaySyncRules.Changes(false, true, true, false, false));
    }

    [Fact]
    public void ChatLines_ArePlain_AndUseTheLabel()
    {
        Assert.Equal("Pairing sync added partner Mira.", RelaySyncRules.Describe(PairSyncChange.PartnerAdded, "Mira"));
        Assert.Equal("Pairing sync made Mira Moon@Ultros an owner.", RelaySyncRules.Describe(PairSyncChange.MadeOwner, "Mira Moon@Ultros"));
        Assert.Equal("Pairing sync allowed PARTNR22 to call you.", RelaySyncRules.Describe(PairSyncChange.AllowedCall, Partner));
    }

    // ---------- 02-D22 and 02-D27: local stamps from the future are reset ----------

    private static PairedPartner Stamped(string key, DateTimeOffset? stamp, string nickname = "") => new()
    {
        PartnerKey = key,
        Nickname = nickname,
        CanWindMe = true,
        CanUnwindMe = true,
        IsOwner = true,
        CanCallMe = true,
        ConsentUpdatedUtc = stamp,
    };

    [Fact]
    public void FutureConsentStamps_TurnTheFlagsOff_AndClearTheStamp()
    {
        var now = DateTimeOffset.UtcNow;
        var forged = Stamped(Partner, now.AddYears(30));
        var reset = RelaySyncRules.ResetFutureLocalStamps([forged], null, now);

        Assert.Equal([forged], reset.Partners);
        Assert.True(reset.Any);
        Assert.False(reset.WindStampCleared);
        Assert.False(forged.CanWindMe);
        Assert.False(forged.CanUnwindMe);
        Assert.False(forged.IsOwner);
        Assert.False(forged.CanCallMe);
        Assert.Null(forged.ConsentUpdatedUtc);
    }

    [Fact]
    public void ConsentStamps_WithinTheSkew_OrMissing_AreLeftAlone()
    {
        var now = DateTimeOffset.UtcNow;
        var atEdge = Stamped("EDGE0001", now + SyncStamp.MaxFutureSkew);
        var past = Stamped("PAST0002", now.AddDays(-2));
        var unstamped = Stamped("NONE0003", null);
        var forged = Stamped("FAKE0004", now + SyncStamp.MaxFutureSkew + TimeSpan.FromSeconds(1));

        var reset = RelaySyncRules.ResetFutureLocalStamps([atEdge, past, unstamped, forged], now.AddMinutes(1), now);

        Assert.Equal([forged], reset.Partners);
        Assert.False(reset.WindStampCleared);
        foreach (var kept in new[] { atEdge, past, unstamped })
        {
            Assert.True(kept.CanWindMe && kept.CanUnwindMe && kept.IsOwner && kept.CanCallMe);
        }

        Assert.Equal(now + SyncStamp.MaxFutureSkew, atEdge.ConsentUpdatedUtc);
        Assert.Null(unstamped.ConsentUpdatedUtc);
    }

    [Fact]
    public void AFutureWindStamp_IsReportedForClearing_OnItsOwn()
    {
        var now = DateTimeOffset.UtcNow;
        var reset = RelaySyncRules.ResetFutureLocalStamps([Stamped(Partner, now)], now.AddHours(1), now);
        Assert.True(reset.WindStampCleared);
        Assert.True(reset.Any);
        Assert.Empty(reset.Partners);

        Assert.False(RelaySyncRules.ResetFutureLocalStamps([], now + SyncStamp.MaxFutureSkew, now).Any);
        Assert.False(RelaySyncRules.ResetFutureLocalStamps([], null, now).Any);
    }

    [Fact]
    public void TheResetLine_IsPlain_AndUsesTheLabel()
    {
        // 02-D27: the next sync may restore them from the relay.
        Assert.Equal(
            "Permissions for Mira had an invalid timestamp; restored from the relay if it has a valid copy, otherwise turned off. Please check them.",
            RelaySyncRules.DescribeStampReset(Stamped(Partner, null, nickname: "Mira").GetChosenName()));
    }

    // ---------- 02-D29: a doll pushes after relay sync makes or restores a pair ----------

    [Fact]
    public void ADoll_PushesAfterAPairIsMadeOrRestoredHere()
    {
        Assert.True(RelaySyncRules.PushesAfterPairing(isDoll: true, pairedHere: true));
    }

    [Theory]
    [InlineData(false, true)] // not a doll: it has no consent rows to push
    [InlineData(true, false)] // skipped here (unpaired on this PC)
    [InlineData(false, false)]
    public void OtherwiseNothingIsPushed(bool isDoll, bool pairedHere)
    {
        Assert.False(RelaySyncRules.PushesAfterPairing(isDoll, pairedHere));
    }

    // ---------- 02-D30: removing a partner clears ownership here ----------

    [Fact]
    public void AnOwnersUnpair_AlsoRemovesTheOwnedDoll()
    {
        var partners = new List<PairedPartner> { new() { PartnerKey = Partner }, new() { PartnerKey = "OTHER333" } };
        var owned = new List<OwnedDoll> { new() { DollKey = "OTHER333" }, new() { DollKey = Partner } };

        var removal = RelaySyncRules.RemovePartnerLocally(partners, owned, " partnr22 ");

        Assert.Equal(new LocalPartnerRemoval(Partner: true, WasOwner: false, OwnedDoll: true), removal);
        Assert.True(removal.Any);
        Assert.Equal(["OTHER333"], partners.Select(p => p.PartnerKey));
        Assert.Equal(["OTHER333"], owned.Select(d => d.DollKey));
    }

    [Fact]
    public void ADollsUnpairOfItsOwner_SaysItWasAnOwner()
    {
        var partners = new List<PairedPartner> { new() { PartnerKey = Partner, IsOwner = true } };
        var owned = new List<OwnedDoll> { new() { DollKey = "OTHER333" } };

        var removal = RelaySyncRules.RemovePartnerLocally(partners, owned, Partner);

        Assert.Equal(new LocalPartnerRemoval(Partner: true, WasOwner: true, OwnedDoll: false), removal);
        Assert.Empty(partners);
        Assert.Single(owned);
    }

    [Fact]
    public void AnOwnedDollThatIsNoLongerAPartner_IsStillRemoved()
    {
        var removal = RelaySyncRules.RemovePartnerLocally([], [new OwnedDoll { DollKey = Partner }], Partner);
        Assert.Equal(new LocalPartnerRemoval(Partner: false, WasOwner: false, OwnedDoll: true), removal);
        Assert.True(removal.Any);
    }

    [Theory]
    [InlineData("SHORT")]
    [InlineData("")]
    [InlineData(null)]
    public void AnInvalidKey_RemovesNothing(string? key)
    {
        // Not even a stored entry with the same (invalid) key: an invalid key names nobody.
        var partners = new List<PairedPartner> { new() { PartnerKey = Partner, IsOwner = true }, new() { PartnerKey = key ?? "" } };
        var owned = new List<OwnedDoll> { new() { DollKey = Partner }, new() { DollKey = key ?? "" } };

        Assert.False(RelaySyncRules.RemovePartnerLocally(partners, owned, key).Any);
        Assert.Equal(2, partners.Count);
        Assert.Equal(2, owned.Count);
    }

    // ---------- 02-D23: consent rows can't add partners ----------

    [Fact]
    public void PairSync_AddsOnlyFromPeerKeys_ConsentOnlyKeysJustUpdate()
    {
        var targets = RelaySyncRules.PairSyncTargets([Partner], [Partner, "CONSENT9"], Me);
        Assert.Equal([new PairSyncTarget(Partner, MayAdd: true), new PairSyncTarget("CONSENT9", MayAdd: false)], targets);

        // Without peerKeys (an old relay's consent-row fallback), nothing may be added.
        Assert.All(RelaySyncRules.PairSyncTargets(null, [Partner, "CONSENT9"], Me), t => Assert.False(t.MayAdd));
    }

    [Fact]
    public void PairSyncTargets_AreNormalizedAndValid_WithoutTheSelfKeyOrDuplicates()
    {
        var targets = RelaySyncRules.PairSyncTargets([" partnr22 ", "SHORT", Me, null, Partner], ["mememe11", "partnr22", ""], Me);
        Assert.Equal([new PairSyncTarget(Partner, MayAdd: true)], targets);
    }
}
