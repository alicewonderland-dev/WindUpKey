using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using WindUpKey.Protocol;

namespace WindUpKey.Services;

/// <summary>What the connect loop does before it opens a socket.</summary>
public enum RelayConnectStep
{
    /// <summary>Connect and register with this character's relay secret.</summary>
    Register,

    /// <summary>No character profile yet: wait, don't register without a secret.</summary>
    WaitForCharacter,

    /// <summary>The identity file can't be read or locked right now: don't register; retry on the normal backoff.</summary>
    WaitIdentityUnavailable,

    /// <summary>The identity file is from a newer build and has no entry for this character: don't register; retry on the normal backoff.</summary>
    WaitIdentityReadOnly,

    /// <summary>
    /// The identity file (or its lock file) can't be written and has no entry for this character: don't register;
    /// retry on the normal backoff.
    /// </summary>
    WaitIdentityNotWritable,

    /// <summary>Every host rejected the identity, or the session was replaced: wait for the user.</summary>
    WaitForUser,
}

/// <summary>The relay auth block, if any.</summary>
public enum RelayAuthBlock
{
    /// <summary>Not blocked.</summary>
    None,

    /// <summary>
    /// Some hosts rejected the identity and the rest were unreachable. The loop keeps trying the unreachable ones
    /// on the normal backoff, and the block lifts when one of them accepts the register.
    /// </summary>
    Partial,

    /// <summary>Every host explicitly rejected the identity, or the session was replaced. Waits for the user.</summary>
    Full,
}

/// <summary>What the connect loop does after a host outcome.</summary>
public enum RelayHostVerdict
{
    /// <summary>The outcome belongs to a cycle that was reset meanwhile (login, character switch, import, revert): ignore it.</summary>
    Ignored,

    /// <summary>This host rejected the identity, but others haven't: try the next one now.</summary>
    TryNextHost,

    /// <summary>An ordinary outage, or still partially blocked: retry on the normal backoff.</summary>
    Retry,

    /// <summary>The block was entered or changed: show <see cref="RelayConnectPolicy.BlockCode"/> (chat only the first time).</summary>
    Blocked,
}

/// <summary>What a relay error or close means for this session (decisions 7 and 12).</summary>
public enum RelaySessionSignal
{
    /// <summary>Not a session-level signal: an ordinary error (or close), handled as before.</summary>
    None,

    /// <summary>A relay-only code in an error that has a <c>to</c> field, so a peer routed it: ignored silently.</summary>
    Ignore,

    /// <summary>This host rejected the identity (auth_failed or auth_required): the connect loop tries the next host.</summary>
    Rejected,

    /// <summary>The relay replaced this session (the character connected elsewhere): block until the user acts.</summary>
    SessionReplaced,
}

/// <summary>The kinds of auth block, each with its own chat quieting (decision 33).</summary>
public enum RelayBlockKind
{
    /// <summary>The relay rejected the identity (auth_failed or auth_required).</summary>
    IdentityRejected,

    /// <summary>The relay replaced this session (session_replaced).</summary>
    SessionReplaced,
}

/// <summary>
/// The connect loop's decisions: whether to register at all (decision 23), which hosts to try, and when to enter,
/// keep or lift the auth block (decisions 22 and 24). Each relay host keeps its own accounts, so a rejection is
/// per host. Rejections are remembered until a register is accepted or the cycle is reset.
/// <para>
/// No Dalamud dependency. Thread-safe: the connect loop reads it from a background thread; outcomes and resets are
/// applied on the framework thread. Every outcome carries the <see cref="Epoch"/> it was started in, and outcomes
/// from an earlier epoch are ignored.
/// </para>
/// </summary>
public sealed class RelayConnectPolicy
{
    /// <summary>
    /// How often the block message may go to chat for one character and block kind (decisions 31 and 33). The status
    /// always updates.
    /// </summary>
    public static readonly TimeSpan BlockChatInterval = TimeSpan.FromMinutes(30);

    private readonly object _gate = new();
    private readonly Dictionary<(string Character, RelayBlockKind Kind), DateTime> _blockChatUtc = new();
    private readonly HashSet<string> _rejectedHosts = new(StringComparer.Ordinal);
    private string? _rejectionCode;
    private RelayAuthBlock _block;
    private string? _blockCode;
    private bool _becameFullChatPending;
    private int _epoch;
    private string? _closeStatus;

    /// <summary>The current cycle. Captured when an attempt starts, and passed back with its outcome.</summary>
    public int Epoch
    {
        get
        {
            lock (_gate)
                return _epoch;
        }
    }

    public RelayAuthBlock Block
    {
        get
        {
            lock (_gate)
                return _block;
        }
    }

    /// <summary>The code the block was entered with (auth_failed, auth_required or session_replaced), or null.</summary>
    public string? BlockCode
    {
        get
        {
            lock (_gate)
                return _blockCode;
        }
    }

    /// <summary>
    /// Decides whether to register. Never registers without a secret: a secretless register on a claimed key is
    /// refused (auth_required), which would block with the wrong advice when the real problem is the local file.
    /// </summary>
    public RelayConnectStep BeforeConnect(bool hasCharacter, bool hasSecret, RelayIdentityStoreStatus storeStatus)
    {
        lock (_gate)
        {
            if (_block == RelayAuthBlock.Full)
                return RelayConnectStep.WaitForUser;
        }

        if (!hasCharacter)
            return RelayConnectStep.WaitForCharacter;
        if (hasSecret)
            return RelayConnectStep.Register;
        return storeStatus switch
        {
            RelayIdentityStoreStatus.ReadOnly => RelayConnectStep.WaitIdentityReadOnly,
            RelayIdentityStoreStatus.NotWritable => RelayConnectStep.WaitIdentityNotWritable,
            _ => RelayConnectStep.WaitIdentityUnavailable,
        };
    }

    /// <summary>The candidates to try, in order: those that haven't rejected the identity since the last accepted register.</summary>
    public IReadOnlyList<string> HostsToTry(IReadOnlyList<string> candidates)
    {
        lock (_gate)
            return _block == RelayAuthBlock.Full ? [] : candidates.Where(h => !_rejectedHosts.Contains(h)).ToArray();
    }

    /// <summary><paramref name="host"/> answered the register with auth_failed or auth_required.</summary>
    public RelayHostVerdict Rejected(int epoch, string host, string code, IReadOnlyList<string> candidates)
    {
        lock (_gate)
        {
            if (epoch != _epoch || _block == RelayAuthBlock.Full)
                return RelayHostVerdict.Ignored;

            _rejectedHosts.Add(host);
            // A wrong identity (auth_failed) says more than a missing one (auth_required), so it wins.
            if (_rejectionCode is null || code == ErrorCodes.AuthFailed)
                _rejectionCode = code;

            if (!candidates.All(_rejectedHosts.Contains))
                return RelayHostVerdict.TryNextHost;

            // Every host explicitly rejected it (a partial block becomes full): only the user can fix this.
            EnterFull(_rejectionCode);
            return RelayHostVerdict.Blocked;
        }
    }

    /// <summary>None of the hosts that haven't rejected the identity could be reached.</summary>
    public RelayHostVerdict NoHostReachable(int epoch)
    {
        lock (_gate)
        {
            if (epoch != _epoch || _block == RelayAuthBlock.Full)
                return RelayHostVerdict.Ignored;

            // No rejection: an ordinary outage. Already partially blocked: keep probing.
            if (_rejectedHosts.Count == 0 || _block == RelayAuthBlock.Partial)
                return RelayHostVerdict.Retry;

            // Every host that answered rejected it, the rest are down: block, but keep probing those.
            _block = RelayAuthBlock.Partial;
            _blockCode = _rejectionCode;
            return RelayHostVerdict.Blocked;
        }
    }

    /// <summary>Enters the full block. Call with the lock held. A partial block that becomes full owes one chat line (decision 37).</summary>
    private void EnterFull(string? code)
    {
        _becameFullChatPending = _block == RelayAuthBlock.Partial;
        _block = RelayAuthBlock.Full;
        _blockCode = code;
    }

    /// <summary>
    /// The relay accepted the register. Forgets the rejections (even if the session later ends in an error), and
    /// lifts a partial block. Returns true when it lifted one. A full block is never lifted here.
    /// </summary>
    public bool Accepted(int epoch)
    {
        lock (_gate)
        {
            if (epoch != _epoch || _block == RelayAuthBlock.Full)
                return false;

            _rejectedHosts.Clear();
            _rejectionCode = null;
            if (_block != RelayAuthBlock.Partial)
                return false;

            _block = RelayAuthBlock.None;
            _blockCode = null;
            return true;
        }
    }

    /// <summary>The relay replaced this session (the character connected elsewhere). Returns true when this entered the block.</summary>
    public bool SessionReplaced(int epoch)
    {
        lock (_gate)
        {
            if (epoch != _epoch || _block == RelayAuthBlock.Full)
                return false;

            EnterFull(ErrorCodes.SessionReplaced);
            return true;
        }
    }

    /// <summary>
    /// What the block tells the player (status line and chat). When the identity was rejected and a copy of a damaged
    /// identity file was kept aside (<paramref name="backupFileName"/>, a file name only), it says the copy may hold
    /// the secret to import. Never contains a secret.
    /// </summary>
    public static string BlockStatus(string code, RelayAuthBlock block, string? backupFileName)
    {
        var status = code switch
        {
            ErrorCodes.AuthFailed =>
                "The relay rejected this character's relay identity. If you use Wind-Up Key on another PC, " +
                "copy the relay identity there and import it here (Settings, Connection). " +
                "If you no longer have it, ask the relay operator to reset your pairing key, then press Reconnect.",
            ErrorCodes.AuthRequired =>
                "The relay needs this character's relay identity. Import it in Settings, under Connection, or press Reconnect.",
            _ =>
                "This character connected to the relay from somewhere else, so this session was closed. " +
                "Press Reconnect to take it back.",
        };
        if (code != ErrorCodes.SessionReplaced && !string.IsNullOrEmpty(backupFileName))
        {
            status += $" An older copy of the relay identity file was kept as {backupFileName} in the pluginConfigs " +
                      "folder, and it may contain the secret to import.";
        }

        if (block == RelayAuthBlock.Partial)
            status += " Still trying the relay hosts that couldn't be reached.";
        return status;
    }

    /// <summary>The kind of block a block code (<see cref="BlockCode"/>) belongs to.</summary>
    public static RelayBlockKind BlockKind(string code) =>
        code == ErrorCodes.SessionReplaced ? RelayBlockKind.SessionReplaced : RelayBlockKind.IdentityRejected;

    /// <summary>
    /// Whether the block message may go to chat now for <paramref name="character"/> and <paramref name="kind"/>
    /// (decisions 31 and 33): at most once per <see cref="BlockChatInterval"/> for each pair, however often it is
    /// blocked, reconnected or reset meanwhile; a block of the other kind still gets its own line. Records the message
    /// when true. A clock that went back doesn't keep it quiet.
    /// </summary>
    public bool TryClaimBlockChat(string character, RelayBlockKind kind, DateTime utcNow)
    {
        lock (_gate)
        {
            var key = (character, kind);
            if (_blockChatUtc.TryGetValue(key, out var last) && utcNow >= last && utcNow - last < BlockChatInterval)
                return false;
            _blockChatUtc[key] = utcNow;
            return true;
        }
    }

    /// <summary>
    /// Whether the block just shown (entered, or changed from partial to full) goes to chat for
    /// <paramref name="character"/>. A partial block ("still trying") that became full (now waiting for the user)
    /// gets one line even inside the quiet window, once per full block (decision 37). Otherwise only a newly entered
    /// block (<paramref name="newBlock"/>) may, through <see cref="TryClaimBlockChat"/>. Either way a line is
    /// recorded as the latest for the character and the block's kind.
    /// </summary>
    public bool ClaimBlockChat(string character, bool newBlock, DateTime utcNow)
    {
        lock (_gate)
        {
            if (_block == RelayAuthBlock.None || _blockCode is not { } code)
                return false;
            var kind = BlockKind(code);
            if (_becameFullChatPending && _block == RelayAuthBlock.Full)
            {
                _becameFullChatPending = false;
                _blockChatUtc[(character, kind)] = utcNow;
                return true;
            }

            return newBlock && TryClaimBlockChat(character, kind, utcNow);
        }
    }

    /// <summary>
    /// Whether the relay identity UI may act on the active character profile (decision 38). It becomes ready once
    /// the local player's ContentId has been read and made the active profile, and stays ready through zone loads
    /// and teleports (no local player for a moment) until logout. A character switch needs a logout, so the profile
    /// can't go stale meanwhile.
    /// </summary>
    public static bool IdentityCharacterReady(bool loggedIn, bool localCharacterRead, bool wasReady) =>
        loggedIn && (localCharacterRead || wasReady);

    /// <summary>
    /// 06-A9: whether an inbound message, queued for the framework thread on the connection opened in
    /// <paramref name="arrivedEpoch"/> for the profile <paramref name="arrivedProfile"/> (ContentId hex), may still be
    /// handled. A character switch closes that socket, but a message already queued would otherwise run after it, and
    /// apply to the new character. It is dropped once the connect cycle moved on (a switch, login, Reconnect, import
    /// or revert all start a new epoch) or another profile is active. A connection always registers for a profile,
    /// so an empty one never matches.
    /// </summary>
    public static bool InboundStillCurrent(int arrivedEpoch, string? arrivedProfile, int currentEpoch, string? activeProfile) =>
        arrivedEpoch == currentEpoch
        && !string.IsNullOrEmpty(arrivedProfile)
        && string.Equals(arrivedProfile, activeProfile, StringComparison.Ordinal);

    /// <summary>
    /// What an error envelope from the relay connection means for the session (decision 7). The session-level codes
    /// only the relay may send (<see cref="ErrorCodes.IsRelayOnly"/>) count only when the error has no <c>to</c>:
    /// relay-originated errors never set one, and routed (peer-sent) errors always do, so those are ignored. Relay-only
    /// codes that aren't auth or session_replaced (pair_key_collision, rate_limited) and every other code are
    /// <see cref="RelaySessionSignal.None"/>: handled as ordinary errors.
    /// </summary>
    public static RelaySessionSignal ClassifyError(string? code, string? to)
    {
        if (!ErrorCodes.IsRelayOnly(code))
            return RelaySessionSignal.None;
        if (!string.IsNullOrEmpty(to))
            return RelaySessionSignal.Ignore;
        return code switch
        {
            ErrorCodes.SessionReplaced => RelaySessionSignal.SessionReplaced,
            ErrorCodes.AuthFailed or ErrorCodes.AuthRequired => RelaySessionSignal.Rejected,
            _ => RelaySessionSignal.None,
        };
    }

    /// <summary>
    /// What the relay closing the socket means for the session (decision 12): relay refusals close with
    /// PolicyViolation and the code as the description (a secretless refusal sends no error first). Any other close
    /// is <see cref="RelaySessionSignal.None"/>, including the 03 limit closes (<see cref="WireCloseReasons"/>), so none
    /// of them causes an auth block (03-D14).
    /// </summary>
    public static RelaySessionSignal ClassifyClose(WebSocketCloseStatus? status, string? description)
    {
        if (status != WebSocketCloseStatus.PolicyViolation)
            return RelaySessionSignal.None;
        return description switch
        {
            ErrorCodes.AuthFailed or ErrorCodes.AuthRequired => RelaySessionSignal.Rejected,
            ErrorCodes.SessionReplaced => RelaySessionSignal.SessionReplaced,
            _ => RelaySessionSignal.None,
        };
    }

    /// <summary>What the config window shows while the relay refuses registers that would create an account (05-D9).</summary>
    public const string BindRateLimitedStatus =
        "The relay is limiting new accounts from your network right now; retrying automatically.";

    /// <summary>
    /// The status a relay close explains (05-D9), or null when it explains none: the first-bind limit's close (1013
    /// <see cref="WireCloseReasons.BindRateLimited"/>) shows <see cref="BindRateLimitedStatus"/> until a register is
    /// accepted. Status only: no chat line and no block (<see cref="ClassifyClose"/> stays None), and the normal backoff
    /// applies.
    /// </summary>
    public static string? CloseStatus(WebSocketCloseStatus? status, string? description) =>
        status == (WebSocketCloseStatus)1013 && description == WireCloseReasons.BindRateLimited
            ? BindRateLimitedStatus
            : null;

    /// <summary>
    /// What the relay's last close explained in the current cycle (05-D9), shown while reconnecting; null when it
    /// explained nothing, or once a register is accepted, the host can't be reached, a connection ends without a close
    /// (08-T5), or the cycle is reset.
    /// </summary>
    public string? CurrentCloseStatus
    {
        get
        {
            lock (_gate)
                return _closeStatus;
        }
    }

    /// <summary>
    /// The relay closed a connection started in <paramref name="epoch"/>: keep what the close explains
    /// (<see cref="CloseStatus"/>), or clear it for any other close. 05-D13: a close from an earlier cycle (a character
    /// switch, login, Reconnect, import or revert since) is ignored, so its message never carries into the new cycle.
    /// </summary>
    public void RecordClose(int epoch, WebSocketCloseStatus? status, string? description)
    {
        var explained = CloseStatus(status, description);
        lock (_gate)
        {
            if (epoch == _epoch)
                _closeStatus = explained;
        }
    }

    /// <summary>A register was accepted, or the host can't be reached: the last close no longer explains the status.</summary>
    public void ClearCloseStatus()
    {
        lock (_gate)
            _closeStatus = null;
    }

    /// <summary>Login, Reconnect, character switch, import or revert: lift any block and start a new cycle (the chat throttle stays).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _epoch++;
            _rejectedHosts.Clear();
            _rejectionCode = null;
            _block = RelayAuthBlock.None;
            _blockCode = null;
            // 05-D13: a new cycle starts without the last one's close status.
            _closeStatus = null;
        }
    }
}

/// <summary>
/// The connect loop's reconnect delay (03-D14). It doubles after every failed or ended connection up to
/// <see cref="Max"/>, and goes back to <see cref="Initial"/> only when a register is accepted (the relay's pairSync),
/// or on <see cref="Reset"/> (login, Reconnect). An open socket alone isn't a reason: a host that keeps closing the
/// client before accepting it (a limit, a refusal) is retried on the normal backoff, not every second. Thread-safe.
/// </summary>
public sealed class RelayReconnectBackoff
{
    public static readonly TimeSpan Initial = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan Max = TimeSpan.FromSeconds(30);

    private long _delayTicks = Initial.Ticks;

    /// <summary>The delay the next <see cref="Next"/> returns.</summary>
    public TimeSpan Current => TimeSpan.FromTicks(Interlocked.Read(ref _delayTicks));

    /// <summary>The delay to wait before the next attempt; the one after it is twice as long, up to <see cref="Max"/>.</summary>
    public TimeSpan Next()
    {
        while (true)
        {
            var current = Interlocked.Read(ref _delayTicks);
            var doubled = Math.Min(current * 2, Max.Ticks);
            if (Interlocked.CompareExchange(ref _delayTicks, doubled, current) == current)
                return TimeSpan.FromTicks(current);
        }
    }

    /// <summary>The relay accepted this connection's register.</summary>
    public void RegisterAccepted() => Reset();

    /// <summary>Back to <see cref="Initial"/>.</summary>
    public void Reset() => Interlocked.Exchange(ref _delayTicks, Initial.Ticks);
}

/// <summary>
/// 06-A12: <see cref="RelayClient.ProfileActivated"/> fires once per login or plugin load. "Raised for this login" is
/// tracked on its own, apart from the character-ready state (which a client-state read error clears), and is cleared
/// only when <c>IsLoggedIn</c> is read as false. No Dalamud dependency; framework thread only (not thread-safe).
/// </summary>
public sealed class ProfileActivationLatch
{
    /// <summary>Whether the event was raised since the last logout (or the plugin load).</summary>
    public bool Raised { get; private set; }

    /// <summary><c>IsLoggedIn</c> was read as false: the next activation belongs to a new login.</summary>
    public void LoggedOut() => Raised = false;

    /// <summary>
    /// The logged-in character's profile was activated this tick (<paramref name="profileActive"/>): true the first time
    /// since the last <see cref="LoggedOut"/>, when the caller raises the event; false otherwise.
    /// </summary>
    public bool TryRaise(bool profileActive)
    {
        if (!profileActive || Raised)
            return false;

        Raised = true;
        return true;
    }
}
