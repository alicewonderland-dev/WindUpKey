using System.Text.RegularExpressions;
using WindUpKey.Services;

namespace WindUpKey.Tests;

/// <summary>08-P1 (K3d): the shared fire-and-forget helper.</summary>
public sealed class FireAndForgetTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public void AFault_ReachesTheHandler_AsTheInnerException()
    {
        var inner = new InvalidOperationException("boom");
        var tcs = new TaskCompletionSource();
        Exception? seen = null;
        using var called = new ManualResetEventSlim();
        FireAndForget.Run(tcs.Task, ex =>
        {
            seen = ex;
            called.Set();
        });

        tcs.SetException(inner);
        Assert.True(called.Wait(Wait));
        Assert.Same(inner, seen);
    }

    [Fact]
    public async Task AnAsyncMethodThatThrows_ReachesTheHandler_AsWhatItThrew()
    {
        var inner = new TimeoutException("late");
        var seen = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        FireAndForget.Run(ThrowsAsync(inner), ex => seen.TrySetResult(ex));
        Assert.Same(inner, await seen.Task.WaitAsync(Wait));

        static async Task ThrowsAsync(Exception ex)
        {
            await Task.Yield();
            throw ex;
        }
    }

    [Fact]
    public async Task SuccessAndCancellation_CallNothing()
    {
        var calls = 0;
        void OnFault(Exception _) => Interlocked.Increment(ref calls);

        FireAndForget.Run(Task.CompletedTask, OnFault);
        FireAndForget.Run(Task.FromCanceled(new CancellationToken(canceled: true)), OnFault);
        var later = new TaskCompletionSource();
        var cancelledLater = new TaskCompletionSource();
        FireAndForget.Run(later.Task, OnFault);
        FireAndForget.Run(cancelledLater.Task, OnFault);
        later.SetResult();
        cancelledLater.SetCanceled();

        // The continuation runs synchronously where the task completes; leave the pool a moment anyway.
        await Task.Delay(100);
        Assert.Equal(0, Volatile.Read(ref calls));
    }

    [Fact]
    public void NoSendIsLeftUnobserved_AndRelayClientForgetLogsByKind()
    {
        // K3d: every `_ = SomethingAsync()` outside the tests went through a Forget (CallTravelService's since 07).
        var root = ProfileSettingsTests.PluginPath(".");
        var unprotected = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"_\s*=\s*[\w.]+Async\(").Select(m => $"{Path.GetFileName(f)}: {m.Value}"))
            .ToList();
        Assert.Empty(unprotected);

        // D8: Warning for a player's action, Debug for background work; the message carries only the constant.
        var client = ProfileSettingsTests.PluginSource(Path.Combine("Services", "RelayClient.cs"));
        var forget = ProfileSettingsTests.MethodBody(client, "    internal void Forget(Task task, string what, bool background = false)");
        Assert.Contains("FireAndForget.Run(task,", forget);
        Assert.Contains("if (background)\n                _log.Debug(ex, \"WindUpKey {What} failed\", what);\n            else\n                _log.Warning(ex, \"WindUpKey {What} failed\", what);", forget);
        foreach (var background in new[] { "RequestPresenceAsync(key), \"presence query\"", "PushDollStateAsync(), \"doll-state push\"", "SendOwnerRevokedAsync(peerKey), \"owner revoke\"" })
            Assert.Contains($"Forget({background}, background: true);", client);
        Assert.DoesNotContain("Task.Run(() => Forget", client);

        var call = ProfileSettingsTests.PluginSource(Path.Combine("Services", "CallTravelService.cs"));
        Assert.Contains("FireAndForget.Run(task, ex => _log.Debug(ex, \"Call {What} failed\", what));", call);
    }
}

/// <summary>
/// Issue 08's plugin fixes whose wiring needs Dalamud (<c>RelayClient</c>, the windows): checked by reading their source.
/// The Dalamud-free rules are in <see cref="RelaySyncRulesTests"/>, <see cref="RelayConnectPolicyTests"/> and
/// <see cref="SelfTestSuiteTests"/>.
/// </summary>
public sealed class SmallFixesSourceTests
{
    private static string Client => ProfileSettingsTests.PluginSource(Path.Combine("Services", "RelayClient.cs"));

    // ---------- 08-P2 (K3a, D6): Dispose never blocks ----------

    [Fact]
    public void Dispose_NeverWaits_AndLeavesTheSendLock()
    {
        var dispose = ProfileSettingsTests.MethodBody(Client, "    public void Dispose()");
        foreach (var blocking in new[] { ".Wait(", ".Result", "GetResult()", "_loopTask" })
            Assert.DoesNotContain(blocking, dispose);
        Assert.Contains("_disposed = true;", dispose);
        Assert.DoesNotContain("_sendLock.Dispose", Client);
    }

    [Theory]
    [InlineData("    public async Task CancelPendingPairAsync(")]
    [InlineData("    private async Task SendQueuedPairRemovalAsync(")]
    [InlineData("    private async Task PushDollStateAsync(")]
    // 09-K4, 09-K5: the send results.
    [InlineData("    public async Task SendWindByKeyAsync(")]
    [InlineData("    public async Task SendUnwindByKeyAsync(")]
    [InlineData("    public async Task SendWindRequestAsync(")]
    public void TicksWithoutAToken_DoNothingAfterDispose(string signature)
    {
        // These ticks take no cancellation token, so one queued before an unload can still run after it.
        var body = ProfileSettingsTests.MethodBody(Client, signature);
        Assert.Matches(@"RunOnTick\(\(\) =>\s*\{\s*(//[^\n]*\n\s*)*if \(_disposed\)\s*return( null)?;", body);
    }

    // ---------- 08-P2 (T5): a connection ending without a close clears the bind-limit status ----------

    [Fact]
    public void TheReceiveLoop_RecordsNoClose_WhenItEndsWithoutOne()
    {
        var loop = ProfileSettingsTests.MethodBody(Client, "    private async Task<string?> ReceiveLoopAsync(");
        Assert.Contains("var closeSeen = false;\n        try\n        {\n            while (", loop);
        Assert.Contains("_connectPolicy.RecordClose(epoch, inbound.CloseStatus, inbound.CloseDescription);\n                    closeSeen = true;", loop);
        Assert.Contains("        finally\n        {\n            if (!closeSeen)\n                _connectPolicy.RecordClose(epoch, null, null);\n        }", loop);
        Assert.DoesNotContain("ClearCloseStatus", loop);
    }

    // ---------- 08-D19: a presence "not paired" removes the partner here only, never on the relay ----------

    [Fact]
    public void APresenceNotPaired_RemovesThePartnerHereOnly_AndNeverUnpairsOnTheRelay()
    {
        // Released clients (0.1.2.0 to 0.2.0.1) never sync their pair list, so on a second PC or after a reinstall they
        // answer false to every query: a relay unpair driven by these replies would remove pairs that are still valid.
        var client = Client;
        var handler = ProfileSettingsTests.MethodBody(client, "    private void HandlePresenceResult(");
        // As on 15d2a7c: removed here, and the next pairSync brings the partner back until their own queued unpair lands.
        Assert.Contains("if (payload.StillPaired == false)\n            SilentRemovePartner(peerKey);", handler);
        foreach (var unpair in new[]
                 {
                     "PairRemovalQueue", "PendingPairRemovals", "SendQueuedPairRemovalAsync", "PairRemove", "SendEnvelopeAsync",
                     "SendOwnerRevokedAsync", "Forget(", "PluginChat", "_notifier", "_chat",
                 })
            Assert.DoesNotContain(unpair, handler);
        // Nor through the local removal it calls.
        var silent = ProfileSettingsTests.MethodBody(client, "    private void SilentRemovePartner(");
        Assert.DoesNotContain("PairRemovalQueue", silent);
        Assert.DoesNotContain("Send", silent);

        // Nor through a helper: the player's own unpair is the only place anything queues or sends a pair removal.
        var plugin = ProfileSettingsTests.PluginPath(".");
        var queued = Directory.EnumerateFiles(plugin, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Sum(f => Regex.Matches(File.ReadAllText(f), @"PairRemovalQueue\.Queue\(").Count);
        Assert.Equal(1, queued);
        Assert.Contains("PairRemovalQueue.Queue(",
            ProfileSettingsTests.MethodBody(client, "    public async Task UnpairByKeyAsync("));
        Assert.Equal(2, Regex.Matches(client, @"SendQueuedPairRemovalAsync\(").Count);

        // The confirmation and its follow-up query are gone with it.
        foreach (var dropped in new[] { "PresenceUnpairConfirmation", "_presenceUnpairs", "SendPresenceUnpairFollowUps", "_connectionSerial" })
            Assert.DoesNotContain(dropped, client);
    }

    [Fact]
    public void ThePlayersUnpair_SendsThroughTheSharedTail_WithItsGateIntact()
    {
        // The send tail stays extracted (08-P3), with its accepted gate and same-character completion.
        var unpair = ProfileSettingsTests.MethodBody(Client, "    public async Task UnpairByKeyAsync(");
        Assert.Contains("await SendQueuedPairRemovalAsync(removal, peerKey).ConfigureAwait(false);", unpair);
        var tail = ProfileSettingsTests.MethodBody(Client, "    private async Task SendQueuedPairRemovalAsync(");
        Assert.Matches(@"\{\s*(//[^\n]*\n\s*)*var accepted = _registerAccepted;\s*var contentId = _config.ActiveContentId;\s*try", tail);
        Assert.Contains("if (removal.RevokeOwner && !await SendOwnerRevokedAsync(peerKey).ConfigureAwait(false))", tail);
        Assert.Contains("if (!sent || !accepted)\n                return;", tail);
        Assert.Contains("string.Equals(_config.ActiveContentId, contentId, StringComparison.Ordinal)\n                    && PairRemovalQueue.Complete(_config.PendingPairRemovals, [removal])", tail);
    }

    // ---------- 08-D15: the responder's gate ----------

    [Fact]
    public void APresenceAnswer_LeavesStillPairedUnset_UntilTheRegisterIsAccepted()
    {
        var responder = ProfileSettingsTests.MethodBody(Client, "    private async Task HandleInboundPresenceQueryAsync(");
        Assert.Contains("StillPaired = RelaySyncRules.PresenceStillPairedAnswer(stillPaired, _registerAccepted),", responder);
        Assert.DoesNotContain("StillPaired = stillPaired,", responder);
    }

    // ---------- 08-P5 (T6, D10): re-query after a safeword refusal, keeping the message ----------

    [Fact]
    public void ASafewordRefusal_ReQueriesTheDoll_AndItsResultKeepsTheMessage()
    {
        var client = Client;
        Assert.Contains(
            "var requery = !OwnerSettingsReQueryPending(dollKey);\n"
            + "                    RestoreOwnerSettingsAfterRefusal(dollKey, message, keepErrorOnNextResult: true);\n"
            + "                    if (requery)\n"
            + "                        Forget(ReQueryOwnerSettingsAsync(dollKey), \"owner settings re-query\", background: true);",
            client);
        // One re-query at a time: a refused re-query (a modified client) can't loop.
        Assert.Contains("_ownerSettingsByDollKey.TryGetValue(dollKey, out var snap) && snap.KeepErrorOnNextResult;",
            ProfileSettingsTests.MethodBody(client, "    private bool OwnerSettingsReQueryPending("));
        var restore = ProfileSettingsTests.MethodBody(client, "    private void RestoreOwnerSettingsAfterRefusal(");
        Assert.Contains("snap.KeepErrorOnNextResult = keepErrorOnNextResult;", restore);

        var result = ProfileSettingsTests.MethodBody(client, "    private void HandleOwnerSettingsResult(");
        // 08-D17: only the re-query's own result keeps it.
        Assert.Contains("var keepError = RelaySyncRules.IsOwnerSettingsReQueryResult(\n"
                        + "                previous is { KeepErrorOnNextResult: true }, previous?.ReQueryRequestId, payload.RequestId);", result);
        Assert.Contains("LastError = RelaySyncRules.OwnerSettingsErrorAfterResult(previous?.LastError, keepError),", result);
        Assert.Contains("RestoreSeq = keepError ? ++_ownerSettingsRestoreSeq : 0,", result);
        // Another error or an ack replaces the message, so a later result doesn't keep a stale one.
        Assert.Contains("snap.KeepErrorOnNextResult = false;", ProfileSettingsTests.MethodBody(client, "    private void SetOwnerSettingsError("));
        Assert.Contains("existing.KeepErrorOnNextResult = false;", ProfileSettingsTests.MethodBody(client, "    private void HandleOwnerSettingsAck("));
    }

    [Fact]
    public void AReQueryThatIsntSent_ClearsTheFlag()
    {
        // 08-D17: no result would come to clear it, and the flag is also the one-at-a-time guard.
        var client = Client;
        var requery = ProfileSettingsTests.MethodBody(client, "    private async Task ReQueryOwnerSettingsAsync(");
        Assert.Contains("var requestId = Guid.NewGuid().ToString(\"N\");\n        SetOwnerSettingsReQuery(dollKey, requestId);\n        var sent = false;", requery);
        Assert.Contains("            sent = await SendOwnerSettingsQueryAsync(dollKey, requestId).ConfigureAwait(false);\n        }\n"
                        + "        finally\n        {\n            if (!sent)\n                ClearOwnerSettingsReQuery(dollKey, requestId);\n        }", requery);
        var clear = ProfileSettingsTests.MethodBody(client, "    private void ClearOwnerSettingsReQuery(");
        Assert.Contains("snap.KeepErrorOnNextResult = false;\n            snap.ReQueryRequestId = null;", clear);
        Assert.Contains("!string.Equals(snap.ReQueryRequestId, requestId, StringComparison.Ordinal)", clear);

        // The query reports whether it went out: false from every path that sends nothing, else the send's own result.
        var send = ProfileSettingsTests.MethodBody(client, "    private async Task<bool> SendOwnerSettingsQueryAsync(");
        Assert.Equal(3, Regex.Matches(send, @"return false;").Count);
        Assert.Contains("return await SendEnvelopeAsync(Envelope.Create(MessageTypes.OwnerSettingsQuery,", send);
        Assert.Contains("snap.ReQueryRequestId = requestId;", ProfileSettingsTests.MethodBody(client, "    private void SetOwnerSettingsReQuery("));
    }

    // ---------- 08-P5 (T7): Ground Sit by name ----------

    [Fact]
    public void NoGroundSitLiteral_IsLeft()
    {
        var root = ProfileSettingsTests.PluginPath(".");
        var literals = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"\(ushort\)\s*52\b|(?<!GroundSit)EmoteId(\s*\{\s*get;\s*set;\s*\})?\s*=\s*52\b")
                .Select(m => $"{Path.GetFileName(f)}: {m.Value}"))
            .ToList();
        Assert.Empty(literals);
        Assert.Contains("public ushort LockEmoteId { get; set; } = CharacterProfile.GroundSitEmoteId;", ProfileSettingsTests.PluginSource("Configuration.cs"));
        Assert.Contains("public ushort EmoteId = CharacterProfile.GroundSitEmoteId;", ProfileSettingsTests.PluginSource(Path.Combine("Ui", "ConfigWindow.cs")));
    }
}
