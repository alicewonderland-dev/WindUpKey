using WindUpKey.Protocol;
using WindUpKey.Services;

namespace WindUpKey.Tests;

/// <summary>The connect loop's decisions: decisions 22 (per host), 23 (store state) and 24 (partial blocks).</summary>
public sealed class RelayConnectPolicyTests
{
    private const string H1 = "wss://one.example/ws";
    private const string H2 = "wss://two.example/ws";
    private static readonly string[] Both = [H1, H2];

    // ---------- decision 23: whether to register at all ----------

    [Fact]
    public void WithASecret_Registers()
    {
        foreach (var status in Enum.GetValues<RelayIdentityStoreStatus>())
            Assert.Equal(RelayConnectStep.Register, new RelayConnectPolicy().BeforeConnect(true, true, status));
    }

    [Theory]
    [InlineData(RelayIdentityStoreStatus.Unavailable, RelayConnectStep.WaitIdentityUnavailable)]
    [InlineData(RelayIdentityStoreStatus.ReadOnly, RelayConnectStep.WaitIdentityReadOnly)]
    [InlineData(RelayIdentityStoreStatus.NotWritable, RelayConnectStep.WaitIdentityNotWritable)]
    [InlineData(RelayIdentityStoreStatus.Available, RelayConnectStep.WaitIdentityUnavailable)]
    public void WithoutASecret_NeverRegisters_AndNeverBlocks(RelayIdentityStoreStatus status, RelayConnectStep expected)
    {
        var policy = new RelayConnectPolicy();
        for (var attempt = 0; attempt < 3; attempt++)
            Assert.Equal(expected, policy.BeforeConnect(true, false, status));
        Assert.Equal(RelayAuthBlock.None, policy.Block);
        Assert.Null(policy.BlockCode);

        // As soon as the file can be read again, it registers.
        Assert.Equal(RelayConnectStep.Register, policy.BeforeConnect(true, true, RelayIdentityStoreStatus.Available));
    }

    [Fact]
    public void WithoutACharacter_Waits_EvenWithAStaleSecret()
    {
        var policy = new RelayConnectPolicy();
        Assert.Equal(RelayConnectStep.WaitForCharacter, policy.BeforeConnect(false, false, RelayIdentityStoreStatus.Available));
        Assert.Equal(RelayConnectStep.WaitForCharacter, policy.BeforeConnect(false, true, RelayIdentityStoreStatus.Available));
    }

    // ---------- decision 22: per host ----------

    [Fact]
    public void FirstHostRejecting_TriesTheNextHost_AndSkipsTheRejectedOne()
    {
        var policy = new RelayConnectPolicy();
        Assert.Equal(RelayHostVerdict.TryNextHost, policy.Rejected(policy.Epoch, H1, ErrorCodes.AuthFailed, Both));
        Assert.Equal([H2], policy.HostsToTry(Both));
        Assert.Equal(RelayAuthBlock.None, policy.Block);
    }

    [Fact]
    public void EveryHostRejecting_BlocksFully_AndWaitsForTheUser()
    {
        var policy = new RelayConnectPolicy();
        policy.Rejected(policy.Epoch, H1, ErrorCodes.AuthFailed, Both);
        Assert.Equal(RelayHostVerdict.Blocked, policy.Rejected(policy.Epoch, H2, ErrorCodes.AuthFailed, Both));
        Assert.Equal(RelayAuthBlock.Full, policy.Block);
        Assert.Equal(ErrorCodes.AuthFailed, policy.BlockCode);
        Assert.Equal(RelayConnectStep.WaitForUser, policy.BeforeConnect(true, true, RelayIdentityStoreStatus.Available));
        Assert.Empty(policy.HostsToTry(Both));
    }

    [Fact]
    public void SingleHost_RejectingBlocksStraightAway()
    {
        var policy = new RelayConnectPolicy();
        Assert.Equal(RelayHostVerdict.Blocked, policy.Rejected(policy.Epoch, H1, ErrorCodes.AuthRequired, [H1]));
        Assert.Equal(RelayAuthBlock.Full, policy.Block);
        Assert.Equal(ErrorCodes.AuthRequired, policy.BlockCode);
    }

    [Theory]
    [InlineData(ErrorCodes.AuthFailed, ErrorCodes.AuthRequired)]
    [InlineData(ErrorCodes.AuthRequired, ErrorCodes.AuthFailed)]
    public void WrongIdentity_WinsOverMissingIdentity_ForTheMessage(string first, string second)
    {
        var policy = new RelayConnectPolicy();
        policy.Rejected(policy.Epoch, H1, first, Both);
        policy.Rejected(policy.Epoch, H2, second, Both);
        Assert.Equal(ErrorCodes.AuthFailed, policy.BlockCode);
    }

    [Fact]
    public void AllRequired_BlocksWithRequired()
    {
        var policy = new RelayConnectPolicy();
        policy.Rejected(policy.Epoch, H1, ErrorCodes.AuthRequired, Both);
        policy.Rejected(policy.Epoch, H2, ErrorCodes.AuthRequired, Both);
        Assert.Equal(ErrorCodes.AuthRequired, policy.BlockCode);
    }

    [Fact]
    public void SameHostRejectingTwice_DoesNotCountAsTheOtherHost()
    {
        var policy = new RelayConnectPolicy();
        Assert.Equal(RelayHostVerdict.TryNextHost, policy.Rejected(policy.Epoch, H1, ErrorCodes.AuthFailed, Both));
        Assert.Equal(RelayHostVerdict.TryNextHost, policy.Rejected(policy.Epoch, H1, ErrorCodes.AuthFailed, Both));
        Assert.Equal(RelayAuthBlock.None, policy.Block);
    }

    [Fact]
    public void Unreachable_WithoutAnyRejection_IsAnOrdinaryOutage()
    {
        var policy = new RelayConnectPolicy();
        Assert.Equal(RelayHostVerdict.Retry, policy.NoHostReachable(policy.Epoch));
        Assert.Equal(RelayAuthBlock.None, policy.Block);
        Assert.Equal(Both, policy.HostsToTry(Both));
    }

    // ---------- decision 24: partial blocks lift themselves ----------

    [Fact]
    public void RejectedThenTheOtherHostsUnreachable_BlocksPartially_AndKeepsProbingThem()
    {
        var policy = new RelayConnectPolicy();
        policy.Rejected(policy.Epoch, H1, ErrorCodes.AuthFailed, Both);
        Assert.Equal(RelayHostVerdict.Blocked, policy.NoHostReachable(policy.Epoch));
        Assert.Equal(RelayAuthBlock.Partial, policy.Block);
        Assert.Equal(ErrorCodes.AuthFailed, policy.BlockCode);

        // Still connects, but only to the host that couldn't be reached.
        Assert.Equal(RelayConnectStep.Register, policy.BeforeConnect(true, true, RelayIdentityStoreStatus.Available));
        Assert.Equal([H2], policy.HostsToTry(Both));

        // Still unreachable: retry on the backoff, without announcing the block again.
        Assert.Equal(RelayHostVerdict.Retry, policy.NoHostReachable(policy.Epoch));
        Assert.Equal(RelayHostVerdict.Retry, policy.NoHostReachable(policy.Epoch));
        Assert.Equal(RelayAuthBlock.Partial, policy.Block);
    }

    [Fact]
    public void PartialBlock_LiftsWhenAProbedHostAccepts()
    {
        var policy = new RelayConnectPolicy();
        policy.Rejected(policy.Epoch, H1, ErrorCodes.AuthFailed, Both);
        policy.NoHostReachable(policy.Epoch);

        Assert.True(policy.Accepted(policy.Epoch));
        Assert.Equal(RelayAuthBlock.None, policy.Block);
        Assert.Null(policy.BlockCode);
        Assert.Equal(Both, policy.HostsToTry(Both));
        Assert.False(policy.Accepted(policy.Epoch)); // lifted once
    }

    [Fact]
    public void PartialBlock_BecomesFullWhenTheProbedHostRejectsToo()
    {
        var policy = new RelayConnectPolicy();
        policy.Rejected(policy.Epoch, H1, ErrorCodes.AuthRequired, Both);
        policy.NoHostReachable(policy.Epoch);

        Assert.Equal(RelayHostVerdict.Blocked, policy.Rejected(policy.Epoch, H2, ErrorCodes.AuthFailed, Both));
        Assert.Equal(RelayAuthBlock.Full, policy.Block);
        Assert.Equal(ErrorCodes.AuthFailed, policy.BlockCode);
        Assert.Equal(RelayConnectStep.WaitForUser, policy.BeforeConnect(true, true, RelayIdentityStoreStatus.Available));
    }

    [Fact]
    public void FullBlock_DoesNotLiftByItself_OnlyAReset()
    {
        var policy = new RelayConnectPolicy();
        policy.Rejected(policy.Epoch, H1, ErrorCodes.AuthFailed, Both);
        policy.Rejected(policy.Epoch, H2, ErrorCodes.AuthFailed, Both);

        Assert.False(policy.Accepted(policy.Epoch));
        Assert.Equal(RelayHostVerdict.Ignored, policy.NoHostReachable(policy.Epoch));
        Assert.Equal(RelayHostVerdict.Ignored, policy.Rejected(policy.Epoch, H1, ErrorCodes.AuthFailed, Both));
        Assert.Equal(RelayAuthBlock.Full, policy.Block);
        Assert.Equal(RelayConnectStep.WaitForUser, policy.BeforeConnect(true, true, RelayIdentityStoreStatus.Available));

        policy.Reset();
        Assert.Equal(RelayAuthBlock.None, policy.Block);
        Assert.Null(policy.BlockCode);
        Assert.Equal(Both, policy.HostsToTry(Both));
        Assert.Equal(RelayConnectStep.Register, policy.BeforeConnect(true, true, RelayIdentityStoreStatus.Available));
    }

    // ---------- accepted register ----------

    [Fact]
    public void AcceptedRegister_ForgetsRejections_EvenWhenTheSessionLaterEndsInAnError()
    {
        var policy = new RelayConnectPolicy();
        var epoch = policy.Epoch;
        policy.Rejected(epoch, H1, ErrorCodes.AuthFailed, Both);
        Assert.False(policy.Accepted(epoch)); // H2 accepted; nothing to lift

        // The session then throws and H2 goes down: an outage, not a block from H1's old rejection.
        Assert.Equal(RelayHostVerdict.Retry, policy.NoHostReachable(epoch));
        Assert.Equal(RelayAuthBlock.None, policy.Block);
        Assert.Equal(Both, policy.HostsToTry(Both));
        Assert.Equal(RelayHostVerdict.TryNextHost, policy.Rejected(epoch, H2, ErrorCodes.AuthFailed, Both));
    }

    // ---------- stale cycles ----------

    [Fact]
    public void StaleCycleOutcomes_AreIgnored()
    {
        var policy = new RelayConnectPolicy();
        var stale = policy.Epoch;
        policy.Reset(); // login / switch / import / revert while the attempt was in flight

        Assert.Equal(RelayHostVerdict.Ignored, policy.Rejected(stale, H1, ErrorCodes.AuthFailed, [H1]));
        Assert.Equal(RelayAuthBlock.None, policy.Block);
        Assert.Equal(Both, policy.HostsToTry(Both));

        policy.Rejected(policy.Epoch, H1, ErrorCodes.AuthFailed, Both);
        Assert.Equal(RelayHostVerdict.Ignored, policy.NoHostReachable(stale));
        Assert.Equal(RelayAuthBlock.None, policy.Block);

        Assert.Equal(RelayHostVerdict.Blocked, policy.NoHostReachable(policy.Epoch));
        Assert.False(policy.Accepted(stale)); // a stale accept doesn't lift the current partial block
        Assert.Equal(RelayAuthBlock.Partial, policy.Block);
        Assert.False(policy.SessionReplaced(stale));
        Assert.Equal(RelayAuthBlock.Partial, policy.Block);
    }

    // ---------- session_replaced ----------

    [Fact]
    public void SessionReplaced_BlocksFullyOnce_UntilReset()
    {
        var policy = new RelayConnectPolicy();
        Assert.True(policy.SessionReplaced(policy.Epoch));
        Assert.False(policy.SessionReplaced(policy.Epoch));
        Assert.Equal(RelayAuthBlock.Full, policy.Block);
        Assert.Equal(ErrorCodes.SessionReplaced, policy.BlockCode);
        Assert.False(policy.Accepted(policy.Epoch));
        Assert.Equal(RelayConnectStep.WaitForUser, policy.BeforeConnect(true, true, RelayIdentityStoreStatus.Available));

        policy.Reset();
        Assert.Equal(RelayAuthBlock.None, policy.Block);
    }

    // ---------- decision 28: the block message points to a kept-aside copy ----------

    private const string Backup = "WindUpKey.relay-identity.json.corrupt-20261011T123456Z";

    [Theory]
    [InlineData(ErrorCodes.AuthFailed)]
    [InlineData(ErrorCodes.AuthRequired)]
    public void RejectedIdentity_WithAKeptAsideCopy_NamesIt_InOneSentence(string code)
    {
        var without = RelayConnectPolicy.BlockStatus(code, RelayAuthBlock.Full, null);
        var with = RelayConnectPolicy.BlockStatus(code, RelayAuthBlock.Full, Backup);

        Assert.StartsWith(without, with);
        var added = with[without.Length..].Trim();
        Assert.Contains(Backup, added);
        Assert.Contains("may contain the secret to import", added);
        Assert.Single(added.Replace(Backup, "NAME", StringComparison.Ordinal), c => c == '.'); // one sentence
        Assert.EndsWith(".", added);
    }

    [Fact]
    public void RejectedIdentity_WithoutACopy_SaysNothingAboutOne()
    {
        Assert.DoesNotContain("older copy", RelayConnectPolicy.BlockStatus(ErrorCodes.AuthFailed, RelayAuthBlock.Full, null));
        Assert.DoesNotContain("older copy", RelayConnectPolicy.BlockStatus(ErrorCodes.AuthFailed, RelayAuthBlock.Full, ""));
    }

    [Fact]
    public void SessionReplaced_DoesNotMentionTheCopy()
    {
        Assert.DoesNotContain(Backup, RelayConnectPolicy.BlockStatus(ErrorCodes.SessionReplaced, RelayAuthBlock.Full, Backup));
    }

    [Fact]
    public void PartialBlock_StillSaysItKeepsTrying_AfterTheCopySentence()
    {
        var status = RelayConnectPolicy.BlockStatus(ErrorCodes.AuthFailed, RelayAuthBlock.Partial, Backup);
        Assert.Contains(Backup, status);
        Assert.EndsWith("Still trying the relay hosts that couldn't be reached.", status);
        Assert.DoesNotContain("Still trying", RelayConnectPolicy.BlockStatus(ErrorCodes.AuthFailed, RelayAuthBlock.Full, Backup));
    }

    // ---------- decision 31: the block message goes to chat at most once per 30 minutes per character ----------

    private static readonly DateTime T0 = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
    private const RelayBlockKind Rejected = RelayBlockKind.IdentityRejected;
    private const RelayBlockKind Replaced = RelayBlockKind.SessionReplaced;

    [Fact]
    public void BlockChat_AtMostOncePer30Minutes_PerCharacter_EvenAcrossResets()
    {
        var policy = new RelayConnectPolicy();
        Assert.True(policy.TryClaimBlockChat("A", Rejected, T0));
        Assert.False(policy.TryClaimBlockChat("A", Rejected, T0.AddMinutes(1)));
        Assert.True(policy.TryClaimBlockChat("B", Rejected, T0.AddMinutes(1))); // another character

        // Reconnect, relog, import: blocked again, but still quiet in chat.
        policy.Reset();
        Assert.False(policy.TryClaimBlockChat("A", Rejected, T0.AddMinutes(29).AddSeconds(59)));

        // A quiet attempt doesn't restart the 30 minutes.
        Assert.True(policy.TryClaimBlockChat("A", Rejected, T0.AddMinutes(30)));
        Assert.False(policy.TryClaimBlockChat("A", Rejected, T0.AddMinutes(59)));
        Assert.True(policy.TryClaimBlockChat("A", Rejected, T0.AddMinutes(60)));
    }

    [Fact]
    public void BlockChat_AClockThatWentBack_DoesNotKeepItQuiet()
    {
        var policy = new RelayConnectPolicy();
        Assert.True(policy.TryClaimBlockChat("A", Rejected, T0));
        Assert.True(policy.TryClaimBlockChat("A", Rejected, T0.AddHours(-1)));
    }

    // ---------- decision 33: the chat quieting is per character and block kind ----------

    [Fact]
    public void BlockChat_ADifferentKind_StillGetsOneLine_TheSameKindStaysQuiet()
    {
        var policy = new RelayConnectPolicy();
        Assert.True(policy.TryClaimBlockChat("A", Rejected, T0));
        Assert.True(policy.TryClaimBlockChat("A", Replaced, T0.AddMinutes(1))); // the other kind: one line
        Assert.False(policy.TryClaimBlockChat("A", Replaced, T0.AddMinutes(2))); // repeats of it stay quiet
        Assert.False(policy.TryClaimBlockChat("A", Rejected, T0.AddMinutes(3)));
        policy.Reset();
        Assert.False(policy.TryClaimBlockChat("A", Rejected, T0.AddMinutes(29)));
        Assert.False(policy.TryClaimBlockChat("A", Replaced, T0.AddMinutes(30)));

        // Each kind's 30 minutes run from its own line.
        Assert.True(policy.TryClaimBlockChat("A", Rejected, T0.AddMinutes(30)));
        Assert.True(policy.TryClaimBlockChat("A", Replaced, T0.AddMinutes(31)));
        Assert.True(policy.TryClaimBlockChat("B", Replaced, T0.AddMinutes(31))); // and per character
    }

    [Theory]
    [InlineData(ErrorCodes.AuthFailed, RelayBlockKind.IdentityRejected)]
    [InlineData(ErrorCodes.AuthRequired, RelayBlockKind.IdentityRejected)]
    [InlineData(ErrorCodes.SessionReplaced, RelayBlockKind.SessionReplaced)]
    public void BlockKind_WrongAndMissingIdentityAreOneKind_SessionReplacedTheOther(string code, RelayBlockKind expected)
    {
        Assert.Equal(expected, RelayConnectPolicy.BlockKind(code));
    }

    [Fact]
    public void BlockChat_WrongThenMissingIdentity_IsTheSameKind_SoItStaysQuiet()
    {
        var policy = new RelayConnectPolicy();
        Assert.True(policy.TryClaimBlockChat("A", RelayConnectPolicy.BlockKind(ErrorCodes.AuthFailed), T0));
        Assert.False(policy.TryClaimBlockChat("A", RelayConnectPolicy.BlockKind(ErrorCodes.AuthRequired), T0.AddMinutes(1)));
        Assert.True(policy.TryClaimBlockChat("A", RelayConnectPolicy.BlockKind(ErrorCodes.SessionReplaced), T0.AddMinutes(2)));
    }

    // ---------- decision 34: the rejection message says to press Reconnect ----------

    [Fact]
    public void WrongIdentity_EndsWithResetThenReconnect()
    {
        const string ending = "ask the relay operator to reset your pairing key, then press Reconnect.";
        Assert.EndsWith(ending, RelayConnectPolicy.BlockStatus(ErrorCodes.AuthFailed, RelayAuthBlock.Full, null));

        // The kept-aside copy (decision 28) and the partial block's note still follow it.
        var partial = RelayConnectPolicy.BlockStatus(ErrorCodes.AuthFailed, RelayAuthBlock.Partial, Backup);
        Assert.Contains(ending + " An older copy", partial);
    }

    // ---------- decision 37: a partial block that becomes full gets one chat line ----------

    private static RelayConnectPolicy PartiallyBlocked(string code = ErrorCodes.AuthFailed)
    {
        var policy = new RelayConnectPolicy();
        policy.Rejected(policy.Epoch, H1, code, Both);
        Assert.Equal(RelayHostVerdict.Blocked, policy.NoHostReachable(policy.Epoch));
        return policy;
    }

    [Fact]
    public void PartialBlockBecomingFull_GetsOneLine_EvenInsideTheQuietWindow_AndNeverRepeatsIt()
    {
        var policy = PartiallyBlocked();
        Assert.True(policy.ClaimBlockChat("A", newBlock: true, T0));

        Assert.Equal(RelayHostVerdict.Blocked, policy.Rejected(policy.Epoch, H2, ErrorCodes.AuthFailed, Both));
        Assert.True(policy.ClaimBlockChat("A", newBlock: false, T0.AddMinutes(1)));

        // The same full block: no second line, not even once the quiet window is over.
        Assert.False(policy.ClaimBlockChat("A", newBlock: false, T0.AddMinutes(2)));
        Assert.False(policy.ClaimBlockChat("A", newBlock: false, T0.AddMinutes(45)));
        Assert.Equal(RelayHostVerdict.Ignored, policy.Rejected(policy.Epoch, H2, ErrorCodes.AuthFailed, Both));
        Assert.False(policy.ClaimBlockChat("A", newBlock: false, T0.AddMinutes(46)));
    }

    [Fact]
    public void PartialBlockBecomingFull_GetsItsLine_WhenThePartialBlockWasQuiet()
    {
        var policy = PartiallyBlocked();
        Assert.True(policy.TryClaimBlockChat("A", Rejected, T0)); // an earlier block's line
        Assert.False(policy.ClaimBlockChat("A", newBlock: true, T0.AddMinutes(5))); // the partial block: quiet

        policy.Rejected(policy.Epoch, H2, ErrorCodes.AuthFailed, Both);
        Assert.True(policy.ClaimBlockChat("A", newBlock: false, T0.AddMinutes(6)));
    }

    [Fact]
    public void BecameFullLine_RestartsTheQuietWindow()
    {
        var policy = PartiallyBlocked();
        policy.Rejected(policy.Epoch, H2, ErrorCodes.AuthFailed, Both);
        Assert.True(policy.ClaimBlockChat("A", newBlock: false, T0));

        policy.Reset();
        policy.Rejected(policy.Epoch, H1, ErrorCodes.AuthFailed, Both);
        policy.Rejected(policy.Epoch, H2, ErrorCodes.AuthFailed, Both);
        Assert.False(policy.ClaimBlockChat("A", newBlock: true, T0.AddMinutes(29)));
        Assert.True(policy.ClaimBlockChat("A", newBlock: true, T0.AddMinutes(30)));
    }

    [Fact]
    public void FullBlockEnteredDirectly_IsNotABecameFullLine_AndStaysQuiet()
    {
        var policy = new RelayConnectPolicy();
        policy.Rejected(policy.Epoch, H1, ErrorCodes.AuthFailed, Both);
        policy.Rejected(policy.Epoch, H2, ErrorCodes.AuthFailed, Both);
        Assert.True(policy.ClaimBlockChat("A", newBlock: true, T0));

        policy.Reset();
        policy.Rejected(policy.Epoch, H1, ErrorCodes.AuthFailed, Both);
        policy.Rejected(policy.Epoch, H2, ErrorCodes.AuthFailed, Both);
        Assert.False(policy.ClaimBlockChat("A", newBlock: true, T0.AddMinutes(5)));
        Assert.False(policy.ClaimBlockChat("A", newBlock: false, T0.AddMinutes(6)));

        // A session replaced without a partial block before it: only the usual quieting.
        policy.Reset();
        Assert.True(policy.SessionReplaced(policy.Epoch));
        Assert.True(policy.ClaimBlockChat("A", newBlock: true, T0.AddMinutes(7)));
        policy.Reset();
        Assert.True(policy.SessionReplaced(policy.Epoch));
        Assert.False(policy.ClaimBlockChat("A", newBlock: true, T0.AddMinutes(8)));
    }

    [Fact]
    public void PartialBlockEndedBySessionReplaced_AlsoGetsOneLine()
    {
        var policy = PartiallyBlocked();
        Assert.True(policy.TryClaimBlockChat("A", Rejected, T0));
        Assert.True(policy.TryClaimBlockChat("A", Replaced, T0));

        Assert.True(policy.SessionReplaced(policy.Epoch));
        Assert.True(policy.ClaimBlockChat("A", newBlock: false, T0.AddMinutes(1)));
        Assert.False(policy.ClaimBlockChat("A", newBlock: false, T0.AddMinutes(2)));
    }

    [Fact]
    public void NewBlock_UsesTheUsualQuieting_PerCharacterAndKind()
    {
        var policy = PartiallyBlocked();
        Assert.True(policy.ClaimBlockChat("A", newBlock: true, T0));
        Assert.False(policy.ClaimBlockChat("A", newBlock: true, T0.AddMinutes(1)));
        Assert.True(policy.ClaimBlockChat("B", newBlock: true, T0.AddMinutes(1)));
        // Not a new block (and not one that became full): never a line.
        Assert.False(policy.ClaimBlockChat("C", newBlock: false, T0.AddMinutes(1)));

        // Not blocked at all: nothing to say.
        Assert.False(new RelayConnectPolicy().ClaimBlockChat("A", newBlock: true, T0));
    }

    // ---------- decision 38: the identity UI keeps its character through zone loads ----------

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, false)] // logged out: never, whatever was read
    [InlineData(false, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, false)] // first zone load after login: not yet
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)] // a later zone load or teleport keeps it
    [InlineData(true, true, true, true)]
    public void IdentityCharacterReady_FromFirstReadUntilLogout(bool loggedIn, bool read, bool wasReady, bool expected)
    {
        Assert.Equal(expected, RelayConnectPolicy.IdentityCharacterReady(loggedIn, read, wasReady));
    }

    [Fact]
    public void IdentityCharacterReady_ThroughALoginZoneLoadAndLogout()
    {
        var ready = false;
        ready = RelayConnectPolicy.IdentityCharacterReady(true, false, ready); // login, loading
        Assert.False(ready);
        ready = RelayConnectPolicy.IdentityCharacterReady(true, true, ready); // in the world
        Assert.True(ready);
        ready = RelayConnectPolicy.IdentityCharacterReady(true, false, ready); // teleport
        Assert.True(ready);
        ready = RelayConnectPolicy.IdentityCharacterReady(false, false, ready); // logout
        Assert.False(ready);
        ready = RelayConnectPolicy.IdentityCharacterReady(true, false, ready); // next login, loading
        Assert.False(ready);
    }

    // ---------- decision 7: relay-only error codes count only without `to`; decision 12: PolicyViolation closes ----------

    [Theory]
    [InlineData(ErrorCodes.AuthFailed)]
    [InlineData(ErrorCodes.AuthRequired)]
    [InlineData(ErrorCodes.SessionReplaced)]
    [InlineData(ErrorCodes.PairKeyCollision)]
    [InlineData(ErrorCodes.RateLimited)]
    public void RelayOnlyCode_RoutedByAPeer_IsIgnored(string code)
    {
        Assert.Equal(RelaySessionSignal.Ignore, RelayConnectPolicy.ClassifyError(code, "ABCD1234"));
    }

    [Theory]
    [InlineData(ErrorCodes.AuthFailed, RelaySessionSignal.Rejected)]
    [InlineData(ErrorCodes.AuthRequired, RelaySessionSignal.Rejected)]
    [InlineData(ErrorCodes.SessionReplaced, RelaySessionSignal.SessionReplaced)]
    [InlineData(ErrorCodes.PairKeyCollision, RelaySessionSignal.None)]
    [InlineData(ErrorCodes.RateLimited, RelaySessionSignal.None)]
    public void RelayOnlyCode_FromTheRelay_IsHonoured(string code, RelaySessionSignal expected)
    {
        Assert.Equal(expected, RelayConnectPolicy.ClassifyError(code, null));
        Assert.Equal(expected, RelayConnectPolicy.ClassifyError(code, ""));
    }

    [Theory]
    [InlineData(ErrorCodes.NotAllowed, "ABCD1234")]
    [InlineData(ErrorCodes.TargetOffline, null)]
    [InlineData(ErrorCodes.Unauthorized, "ABCD1234")]
    [InlineData(null, null)]
    [InlineData("", "ABCD1234")]
    public void OrdinaryCode_IsNeverASessionSignal_WithOrWithoutTo(string? code, string? to)
    {
        Assert.Equal(RelaySessionSignal.None, RelayConnectPolicy.ClassifyError(code, to));
    }

    [Theory]
    [InlineData(ErrorCodes.AuthFailed, RelaySessionSignal.Rejected)]
    [InlineData(ErrorCodes.AuthRequired, RelaySessionSignal.Rejected)]
    [InlineData(ErrorCodes.SessionReplaced, RelaySessionSignal.SessionReplaced)]
    [InlineData("one_key_per_connection", RelaySessionSignal.None)]
    [InlineData(ErrorCodes.PairKeyCollision, RelaySessionSignal.None)]
    [InlineData(null, RelaySessionSignal.None)]
    public void PolicyViolationClose_MapsItsDescription(string? description, RelaySessionSignal expected)
    {
        Assert.Equal(expected, RelayConnectPolicy.ClassifyClose(System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation, description));
    }

    [Theory]
    [InlineData(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure)]
    [InlineData(System.Net.WebSockets.WebSocketCloseStatus.InternalServerError)]
    [InlineData(null)]
    public void OtherClose_IsNeverASessionSignal_WhateverItsDescription(System.Net.WebSockets.WebSocketCloseStatus? status)
    {
        Assert.Equal(RelaySessionSignal.None, RelayConnectPolicy.ClassifyClose(status, ErrorCodes.AuthFailed));
        Assert.Equal(RelaySessionSignal.None, RelayConnectPolicy.ClassifyClose(status, ErrorCodes.SessionReplaced));
    }

    // ---------- 08-T5: a connection that ends without a close ----------

    [Fact]
    public void AConnectionEndingWithoutAClose_ClearsTheBindLimitStatus_OnlyInItsOwnEpoch()
    {
        // The receive loop's finally records "no close" (null, null) when it saw none: the rejected-host path, a dropped
        // socket, a cancel. 05-D13: from an earlier cycle it changes nothing.
        var policy = new RelayConnectPolicy();
        var epoch = policy.Epoch;
        policy.RecordClose(epoch, (System.Net.WebSockets.WebSocketCloseStatus)1013, WireCloseReasons.BindRateLimited);
        Assert.Equal(RelayConnectPolicy.BindRateLimitedStatus, policy.CurrentCloseStatus);
        policy.RecordClose(epoch, null, null);
        Assert.Null(policy.CurrentCloseStatus);

        policy.Reset();
        policy.RecordClose(policy.Epoch, (System.Net.WebSockets.WebSocketCloseStatus)1013, WireCloseReasons.BindRateLimited);
        policy.RecordClose(epoch, null, null);
        Assert.Equal(RelayConnectPolicy.BindRateLimitedStatus, policy.CurrentCloseStatus);
    }
}
