using WindUpKey.Services;

namespace WindUpKey.Tests;

/// <summary>Unpairs queued until the relay has them (02-D9).</summary>
public sealed class PairRemovalQueueTests
{
    private const string Me = "MEMEME11";
    private const string Partner = "PARTNR22";
    private const string Other = "OTHERR33";

    private static PendingPairRemoval Entry(string key, bool revokeOwner = false) =>
        new() { PeerKey = key, RevokeOwner = revokeOwner };

    [Fact]
    public void AnUnpair_IsQueued_WithItsOwnerRevoke()
    {
        var queue = new List<PendingPairRemoval>();
        var sent = PairRemovalQueue.Queue(queue, "partnr-22", Me, revokeOwner: true);

        Assert.NotNull(sent);
        Assert.Equal(Partner, sent.PeerKey);
        Assert.True(sent.RevokeOwner);
        var entry = Assert.Single(queue);
        Assert.Equal(Partner, entry.PeerKey);
        Assert.True(entry.RevokeOwner);
        Assert.True(PairRemovalQueue.Contains(queue, Partner));
    }

    [Theory]
    [InlineData(Me)]
    [InlineData("memEME11")]
    [InlineData("SHORT")]
    [InlineData("")]
    public void TheSelfKey_OrAnInvalidKey_IsNotQueued(string key)
    {
        var queue = new List<PendingPairRemoval>();
        Assert.Null(PairRemovalQueue.Queue(queue, key, Me, revokeOwner: true));
        Assert.Empty(queue);
    }

    [Fact]
    public void QueuingTheSameKeyAgain_KeepsOneEntry_AndItsOwnerRevoke()
    {
        var queue = new List<PendingPairRemoval>();
        PairRemovalQueue.Queue(queue, Partner, Me, revokeOwner: true);
        var again = PairRemovalQueue.Queue(queue, Partner, Me, revokeOwner: false);

        Assert.True(again!.RevokeOwner);
        Assert.True(Assert.Single(queue).RevokeOwner);
    }

    [Fact]
    public void TheEntryToSend_IsACopy()
    {
        var queue = new List<PendingPairRemoval>();
        var sent = PairRemovalQueue.Queue(queue, Partner, Me, revokeOwner: false)!;
        Assert.NotSame(queue[0], sent);

        var toSend = PairRemovalQueue.ToSend(queue, Me);
        Assert.NotSame(queue[0], Assert.Single(toSend));
    }

    [Fact]
    public void PairingAgain_CancelsTheQueuedUnpair()
    {
        var queue = new List<PendingPairRemoval> { Entry(Partner, revokeOwner: true), Entry(Other) };

        Assert.True(PairRemovalQueue.Cancel(queue, "partnr22"));
        Assert.False(PairRemovalQueue.Contains(queue, Partner));
        Assert.True(PairRemovalQueue.Contains(queue, Other));
        Assert.False(PairRemovalQueue.Cancel(queue, Partner));
    }

    [Fact]
    public void ASentEntry_LeavesTheQueue_OthersStay()
    {
        var queue = new List<PendingPairRemoval> { Entry(Partner), Entry(Other) };

        Assert.True(PairRemovalQueue.Complete(queue, [Entry(Partner)]));
        Assert.Equal(Other, Assert.Single(queue).PeerKey);
        Assert.False(PairRemovalQueue.Complete(queue, [Entry(Partner)]));
    }

    [Fact]
    public void AnEntryAskingForAnOwnerRevokeTheSendDidntCarry_Stays()
    {
        var queue = new List<PendingPairRemoval> { Entry(Partner, revokeOwner: true) };

        Assert.False(PairRemovalQueue.Complete(queue, [Entry(Partner, revokeOwner: false)]));
        Assert.Single(queue);
        Assert.True(PairRemovalQueue.Complete(queue, [Entry(Partner, revokeOwner: true)]));
        Assert.Empty(queue);
    }

    [Fact]
    public void ToSend_SkipsTheSelfKey_AndInvalidEntries()
    {
        var queue = new List<PendingPairRemoval> { Entry(Me), Entry("bad"), Entry(Partner, revokeOwner: true) };

        var toSend = PairRemovalQueue.ToSend(queue, Me);
        var only = Assert.Single(toSend);
        Assert.Equal(Partner, only.PeerKey);
        Assert.True(only.RevokeOwner);
    }

    [Fact]
    public void Normalize_CleansWhatAConfigFileHolds()
    {
        List<PendingPairRemoval?> stored =
        [
            null,
            Entry("partnr-22"),
            Entry(Partner, revokeOwner: true),
            Entry("not a key"),
            new() { PeerKey = null! },
            Entry(Other),
        ];

        var clean = PairRemovalQueue.Normalize(stored);

        Assert.Equal([Partner, Other], clean.Select(r => r.PeerKey));
        Assert.True(clean[0].RevokeOwner);
        Assert.False(clean[1].RevokeOwner);
        Assert.Empty(PairRemovalQueue.Normalize(null));
    }
}
