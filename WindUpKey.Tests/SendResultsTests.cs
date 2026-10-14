using System.Text.RegularExpressions;
using WindUpKey.Services;

namespace WindUpKey.Tests;

/// <summary>09-K4: when a wind request that didn't go out gives its cooldown back.</summary>
public sealed class WindRequestCooldownTests
{
    private const string CharacterA = "00112233AABBCCDD";
    private const string CharacterB = "FFEEDDCC44332211";
    private static readonly DateTimeOffset Stamp = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OurStamp_OnTheSameCharacter_IsGivenBack()
    {
        Assert.True(WindRequestCooldown.ShouldRollBack(Stamp, CharacterA, Stamp, CharacterA));
        // The same instant written with another offset is still our stamp.
        Assert.True(WindRequestCooldown.ShouldRollBack(Stamp.ToOffset(TimeSpan.FromHours(9)), CharacterA, Stamp, CharacterA));
    }

    [Fact]
    public void AfterACharacterSwitch_TheCooldownNowSetIsLeftAlone()
    {
        // Even when the other character's cooldown is the very same instant.
        Assert.False(WindRequestCooldown.ShouldRollBack(Stamp, CharacterB, Stamp, CharacterA));
        Assert.False(WindRequestCooldown.ShouldRollBack(Stamp, string.Empty, Stamp, CharacterA));
        Assert.False(WindRequestCooldown.ShouldRollBack(Stamp, CharacterA.ToLowerInvariant(), Stamp, CharacterA));
    }

    [Fact]
    public void ANewerOrClearedCooldown_IsLeftAlone()
    {
        Assert.False(WindRequestCooldown.ShouldRollBack(Stamp.AddTicks(1), CharacterA, Stamp, CharacterA));
        Assert.False(WindRequestCooldown.ShouldRollBack(Stamp.AddMinutes(5), CharacterA, Stamp, CharacterA));
        Assert.False(WindRequestCooldown.ShouldRollBack(null, CharacterA, Stamp, CharacterA));
    }
}

/// <summary>
/// 09-K4, 09-K5: the wind, unwind and wind-request sends read their result. <c>RelayClient</c> needs Dalamud, so the
/// wiring is checked by reading its source.
/// </summary>
public sealed class SendResultsSourceTests
{
    private const string NotConnected = "Not connected yet. Try again in a moment.";
    private const string RequestFailed = "Unable to send that winding request.";
    private const string RequestSent = "Your request for additional winding was sent.";

    private static string Client => ProfileSettingsTests.PluginSource(Path.Combine("Services", "RelayClient.cs"));

    [Theory]
    [InlineData("    public async Task SendWindByKeyAsync(", "Wind", "wind send result")]
    [InlineData("    public async Task SendUnwindByKeyAsync(", "Unwind", "unwind send result")]
    [InlineData("    public async Task SendWindRequestAsync(", "WindRequest", "wind request result")]
    public void EachSend_ReadsItsResult_AndActsOnItInOneGuardedTick(string signature, string type, string what)
    {
        var body = ProfileSettingsTests.MethodBody(Client, signature);

        // The result is kept, and a throw leaves it false (unsent) while still reaching Forget's log.
        var send = "        var sent = false;\n        try\n        {\n"
                   + $"            sent = await SendEnvelopeAsync(Envelope.Create(MessageTypes.{type}, payload), CancellationToken.None)\n"
                   + "                .ConfigureAwait(false);\n        }\n        finally\n        {\n";
        Assert.Contains(send, body);
        Assert.Single(Regex.Matches(body, @"SendEnvelopeAsync\("));
        Assert.DoesNotContain("catch", body);

        // One tick, guarded against an unload (08-P2), logged as background work under a constant (08-P1).
        var (tick, rest) = SplitTick(body, what);
        Assert.Single(Regex.Matches(body, @"RunOnTick\("));
        Assert.Matches(@"^Forget\(_framework\.RunOnTick\(\(\) =>\s*\{\s*(//[^\n]*\n\s*)*if \(_disposed\)\s*return;", tick);

        // After the send, nothing outside that tick touches chat, config or the pending entries.
        var after = rest[rest.IndexOf("SendEnvelopeAsync(", StringComparison.Ordinal)..];
        foreach (var effect in new[] { "_notifier", "PluginChat", "_chat", "_config", "TryTakePendingWind", "_pendingWindRequestIds", "Save()" })
            Assert.DoesNotContain(effect, after);
    }

    [Theory]
    [InlineData("    public async Task SendWindByKeyAsync(", "wind send result", true)]
    [InlineData("    public async Task SendUnwindByKeyAsync(", "unwind send result", false)]
    public void AWindOrUnwindThatDidntGoOut_DropsItsPendingEntry_AndTellsTheWinder(string signature, string what, bool pending)
    {
        var body = ProfileSettingsTests.MethodBody(Client, signature);
        var (tick, _) = SplitTick(body, what);

        // Only an unsent one: a sent wind keeps its pending entry for the relay's result.
        Assert.Contains("        finally\n        {\n            if (!sent)\n            {\n                Forget(_framework.RunOnTick(() =>", body);
        Assert.Contains($"_notifier.NotifyWinderError(\"{NotConnected}\");", tick);
        if (pending)
        {
            Assert.Contains("TryTakePendingWind(payload.RequestId, out _);", tick);
            Assert.True(body.IndexOf("RememberPendingWind(payload.RequestId, hours);", StringComparison.Ordinal)
                        < body.IndexOf("SendEnvelopeAsync(", StringComparison.Ordinal));
        }
        else
        {
            Assert.DoesNotContain("PendingWind", tick);
        }
    }

    [Fact]
    public void AWindRequest_SaysSentOnlyAfterItWent_AndOtherwiseGivesItsHourBack()
    {
        var client = Client;
        var body = ProfileSettingsTests.MethodBody(client, "    public async Task SendWindRequestAsync(");
        var (tick, rest) = SplitTick(body, "wind request result");
        var sendAt = body.IndexOf("SendEnvelopeAsync(", StringComparison.Ordinal);

        // The cooldown still starts before the send (a double click can't send twice), remembering what to give back.
        var start = body.IndexOf(
            "        var previous = _config.LastWindRequestUtc;\n"
            + "        var stamp = DateTimeOffset.UtcNow;\n"
            + "        var contentId = _config.ActiveContentId;\n"
            + "        _config.LastWindRequestUtc = stamp;\n"
            + "        _config.Save();\n",
            StringComparison.Ordinal);
        Assert.InRange(start, 0, sendAt);
        Assert.Single(Regex.Matches(body, @"_config\.LastWindRequestUtc = stamp;"));

        // "Sent" only once it went, on the framework thread: never before the send, never anywhere else.
        Assert.Single(Regex.Matches(client, Regex.Escape(RequestSent)));
        Assert.DoesNotContain(RequestSent, rest);
        Assert.Contains(
            "                if (sent)\n                {\n"
            + $"                    PluginChat.Print(_chat, \"{RequestSent}\", PluginChat.Yellow);\n"
            + "                    return;\n                }\n",
            tick);

        // Unsent: no reply comes, so its id goes; the hour goes back only while it is still our stamp on this character.
        var unsent = tick[tick.IndexOf("                    return;\n                }\n", StringComparison.Ordinal)..];
        Assert.Contains("lock (_pendingWindLock)\n                    _pendingWindRequestIds.Remove(payload.RequestId);", unsent);
        Assert.Contains(
            "if (Services.WindRequestCooldown.ShouldRollBack(\n"
            + "                        _config.LastWindRequestUtc, _config.ActiveContentId, stamp, contentId))\n"
            + "                {\n"
            + "                    _config.LastWindRequestUtc = previous;\n"
            + "                    _config.Save();\n"
            + "                }\n",
            unsent);
        Assert.Single(Regex.Matches(client, @"_config\.LastWindRequestUtc = previous;"));
        Assert.Contains($"_notifier.NotifyWinderError(\"{RequestFailed}\");", unsent);
    }

    /// <summary>
    /// The method's one <c>Forget(_framework.RunOnTick(...), what, background: true)</c>, and the body without it.
    /// </summary>
    private static (string Tick, string Outside) SplitTick(string body, string what)
    {
        var start = body.IndexOf("Forget(_framework.RunOnTick(() =>", StringComparison.Ordinal);
        Assert.True(start >= 0, "no Forget(_framework.RunOnTick(() =>");
        var close = $"}}), \"{what}\", background: true);";
        var end = body.IndexOf(close, start, StringComparison.Ordinal);
        Assert.True(end > start, $"no {close}");
        end += close.Length;
        Assert.True(start > body.IndexOf("SendEnvelopeAsync(", StringComparison.Ordinal), "the tick comes before the send");
        return (body[start..end], body[..start] + body[end..]);
    }
}
