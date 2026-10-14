using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using WindUpKey.Protocol;
#if WINDUP_TESTING
using WindUpKey.SelfTest;
#endif
// 06-A12: Lumina.Excel.Sheets also has an Action (the sheet row); this file means the delegate.
using Action = System.Action;

namespace WindUpKey.Services;

public enum PartnerPresence
{
    Unknown,
    Online,
    Offline,
}

/// <summary>Cached doll settings for the Owner config tab.</summary>
public sealed class OwnerSettingsSnapshot
{
    public string DollKey { get; init; } = string.Empty;
    public double MaxWindHours { get; set; }
    public bool AutoGroundSit { get; set; }
    public ushort LockEmoteId { get; set; }
    public bool SettingsLocked { get; set; }
    public bool CanCall { get; set; }
    public bool TravelReady { get; set; }
    public List<(ushort Id, string Name)> Emotes { get; set; } = [];
    public string? LastError { get; set; }
    public bool HasData { get; set; }

    /// <summary>
    /// 06-B3: changes when the doll refused a settings change because her safeword paused remote control. The Owner
    /// window then drops its draft and shows these values again: the last ones the doll confirmed, which the refused
    /// change never touched.
    /// </summary>
    public long RestoreSeq { get; set; }

    /// <summary>
    /// 08-T6 (D10): set with the re-query after a <c>safeword_paused</c> refusal, so its result keeps
    /// <see cref="LastError"/> (the "paused" message) once. Cleared by that result, another error, or an ack.
    /// 08-D17: also cleared when the re-query isn't sent, so it can't stick and block the next re-query.
    /// </summary>
    internal bool KeepErrorOnNextResult { get; set; }

    /// <summary>
    /// 08-D17: the RequestId of the re-query <see cref="KeepErrorOnNextResult"/> waits for; only its result keeps the
    /// message. Null until the re-query is sent, and cleared with the flag.
    /// </summary>
    internal string? ReQueryRequestId { get; set; }
}

/// <summary>
/// WebSocket client for the wind-up relay. Wind sources should call
/// <see cref="SendWindAsync"/> or <see cref="SendWindByKeyAsync"/>.
/// Game state is sampled on the framework thread via <see cref="Tick"/>; socket IO runs in the background.
/// </summary>
public sealed class RelayClient : IDisposable
{
    public const string GenericWindFailure = "Unable to wind that player.";
    public const string GenericPairFailure = "Pairing could not be completed.";
    public const string GenericOwnerSettingsFailure = "Unable to update that doll's settings.";
    /// <summary>03-D6: the relay refused to queue a message for an offline partner (<c>queue_full</c>).</summary>
    public const string QueueFullFailure = "That partner's message queue is full; try again later.";
    /// <summary>06-B1: the doll refused an unwind or settings update because her safeword paused remote control.</summary>
    public const string SafewordPausedFailure = "That doll has paused remote control for now.";
    public static readonly TimeSpan WindRequestCooldown = TimeSpan.FromHours(1);
    private static readonly TimeSpan PresenceThrottle = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long the relay must stay unreachable (while logged in with identity) before
    /// unwound movement/teleport locks are suspended as a safety fallback.
    /// </summary>
    public static readonly TimeSpan MovementLockSafetyGrace = TimeSpan.FromSeconds(60);

    private readonly Configuration _config;
    private readonly IClientState _clientState;
    private readonly IObjectTable _objectTable;
    private readonly ICondition _condition;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly IChatGui _chat;
    private readonly IDataManager _data;
    private readonly IDalamudPluginInterface _pi;
    private readonly ConsentService _consent;
    private readonly WindTimerService _timer;
    private readonly IWindNotifier _notifier;
    private readonly SoundEffectService _sounds;
    private readonly GameCommandRunner _commands;
    private readonly RelayIdentityStore _identityStore;

    private CallTravelService? _callTravel;
    /// <summary>07-A3: the owner's Calls awaiting a reply (30 min, at most 32); thread-safe (sends come from Draw).</summary>
    private readonly PendingCallBook _pendingCalls = new(() => Environment.TickCount64);
    private const string CallIndoorPlotUnknownFailure = "You can't Call from this house: its plot couldn't be read.";

    private ClientWebSocket? _socket;
    private readonly object _socketGate = new();
    private int _socketGeneration;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    /// <summary>07-A2: how often the relay's "busy" chat line may show.</summary>
    private const long RelayBusyNoticeIntervalMs = 30_000;
    private readonly NoticeThrottle _relayBusyNotice = new(RelayBusyNoticeIntervalMs, () => Environment.TickCount64);
    /// <summary>03-D14: reset when a register is accepted (pairSync), not when the socket opens.</summary>
    private readonly RelayReconnectBackoff _backoff = new();
    private string? _lastStatus;
    private int _running;
    private int _loopGeneration;
    private DateTimeOffset? _unreachableSinceUtc;
    private int _dollStatePushQueued;

    /// <summary>
    /// What to tell the player while the relay rejects this session (session_replaced, or auth_failed /
    /// auth_required from every reachable host). Written on the framework thread from <see cref="_connectPolicy"/>'s
    /// block. Cleared by <see cref="Start"/>, a character switch, <see cref="ImportRelaySecret"/>,
    /// <see cref="RevertRelaySecret"/>, or (partial block only) a host accepting the register.
    /// </summary>
    private volatile string? _authBlockStatus;

    /// <summary>Which hosts rejected the identity, the auth block, and whether to register at all.</summary>
    private readonly RelayConnectPolicy _connectPolicy = new();

    /// <summary>The policy epoch the current connection was opened in (its outcomes are ignored once that is reset).</summary>
    private int _connectionEpoch;

    /// <summary>The host of the current connection; saved as the sticky host once its register is accepted.</summary>
    private volatile string? _connectionUrl;

    /// <summary>The secret sent in the last register. Lets an import of the already-saved value reconnect when it differs. Never log it.</summary>
    private volatile string? _registeredSecret;

    /// <summary>
    /// Set (framework thread) when the relay answers this connection's register with auth_failed/auth_required.
    /// Read by the receive loop, which ends the connection so the connect loop can try the next host.
    /// </summary>
    private volatile string? _registerRejection;

    /// <summary>True once the relay accepted this connection's register (its pairSync arrived). Set on the framework thread.</summary>
    private volatile bool _registerAccepted;

    /// <summary>
    /// The queued unpairs this connection sent right after its register. They leave the profile's queue only once the
    /// register is accepted (pairSync): a host that refuses it ignores them. Written by the connect, taken on the
    /// framework thread.
    /// </summary>
    private SentPairRemovals? _removalsAwaitingSync;

    /// <summary>
    /// Partners whose queued unpair was sent on this connection. This connection's pairSync and queued pairEstablished
    /// were made before the relay had it, so they must not bring them back. Framework thread.
    /// </summary>
    private readonly HashSet<string> _unpairedThisConnection = new(StringComparer.Ordinal);

    private sealed record SentPairRemovals(int Epoch, string PairingKey, IReadOnlyList<PendingPairRemoval> Removals);

    /// <summary>The profile (ContentId) whose local sync stamps were last checked for future dates. Framework thread.</summary>
    private string? _stampsCheckedProfile;

    private readonly object _presenceLock = new();
    private readonly Dictionary<string, PartnerPresence> _presenceByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _presenceLastQueryUtc = new(StringComparer.Ordinal);

    /// <summary>Pending wind requestId → hours sent (winder SFX on windResult).</summary>
    private readonly object _pendingWindLock = new();
    private readonly Dictionary<string, double> _pendingWindHoursByRequestId = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingWindRequestIds = new(StringComparer.Ordinal);

    private readonly object _ownerSettingsLock = new();
    private readonly Dictionary<string, OwnerSettingsSnapshot> _ownerSettingsByDollKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string DollKey, bool NotifyOnAck)> _pendingOwnerRequests = new(StringComparer.Ordinal);
    private long _ownerSettingsRestoreSeq;
    private readonly object _emoteCacheLock = new();
    private List<OwnerEmoteInfo> _cachedUnlockedEmotes = [];

    // Written on framework thread, read by background loop — never touch Dalamud APIs off-thread.
    private volatile bool _loggedIn;

    /// <summary>
    /// True once this login's local player ContentId is the active profile (not the title screen), and until logout:
    /// zone loads and teleports keep it.
    /// </summary>
    private volatile bool _characterReady;
    private volatile string? _cachedIdentity;

    /// <summary>
    /// 06-A2: raised from <see cref="Tick"/> (framework thread) on the first successful profile activation after each
    /// login or plugin load, once the logged-in character's profile is the active one. Login work that reads the
    /// profile (the login sit, the low-wind reminder, Moodles) runs from here, not from the Login event, which fires
    /// before the character can be activated.
    /// </summary>
    public event Action? ProfileActivated;

    /// <summary>06-A12: whether <see cref="ProfileActivated"/> was raised for this login. Framework thread only.</summary>
    private readonly ProfileActivationLatch _profileActivation = new();

    /// <summary>06-A9: 1 once a message dropped from a superseded connection was logged (once per connection, at Debug).</summary>
    private int _staleInboundLogged;
    private int _tickLogCounter;

    /// <summary>Set by <see cref="Dispose"/>: <see cref="Start"/> is a no-op from then on (04-D10).</summary>
    private volatile bool _disposed;

#if WINDUP_TESTING
    // ---------- 04 self-test hooks (Testing builds only, 04-D1) ----------

    /// <summary>04-D6: consulted by the receive loop before the framework thread; null when no run is active.</summary>
    private volatile SelfTestTap? _selfTestTap;

    /// <summary>04-D7 outage: the connect loop doesn't connect while set. Memory only, never persisted.</summary>
    private volatile bool _selfTestOutageHold;

    /// <summary>04-D6: bumped on every accepted register (pairSync), so the self-test can tell the session changed.</summary>
    private int _acceptedConnections;

    /// <summary>04-D6: every session_replaced seen, whether or not it blocked.</summary>
    private int _sessionReplacedSeen;

    /// <summary>04-D7: the relay's last close of the live connection (code and description) and how many there were.</summary>
    private int _closeCount;
    private int _lastCloseStatus = -1;
    private volatile string? _lastCloseDescription;

    /// <summary>04 self-test: the <c>peerKeys</c> of this connection's pairSync (null until one arrives).</summary>
    private volatile IReadOnlyList<string>? _lastPeerKeys;

    /// <summary>04-D11: the pairing key this connection's register carried, recorded when it was sent.</summary>
    private volatile string? _selfTestRegisteredKey;
#endif

    public RelayClient(
        Configuration config,
        IClientState clientState,
        IObjectTable objectTable,
        ICondition condition,
        IFramework framework,
        IPluginLog log,
        IChatGui chat,
        IDataManager data,
        IDalamudPluginInterface pi,
        ConsentService consent,
        WindTimerService timer,
        IWindNotifier notifier,
        SoundEffectService sounds,
        GameCommandRunner commands)
    {
        _config = config;
        _clientState = clientState;
        _objectTable = objectTable;
        _condition = condition;
        _framework = framework;
        _log = log;
        _chat = chat;
        _data = data;
        _pi = pi;
        _consent = consent;
        _timer = timer;
        _notifier = notifier;
        _sounds = sounds;
        _commands = commands;
        _identityStore = new RelayIdentityStore(pi.GetPluginConfigDirectory(), pi.InternalName, message => log.Warning(message));
        _timer.DollWindChanged = QueueDollStatePush;
    }

    public void AttachCallTravel(CallTravelService callTravel) => _callTravel = callTravel;

    public void TickCallTravel() => _callTravel?.Tick();

    public async Task SendCallAsync(string dollKey)
    {
        var key = PairingKeyUtil.Normalize(dollKey);
        if (!PairingKeyUtil.IsValid(key))
            return;

        if (_objectTable.LocalPlayer is not { } player)
            return;

        if (!IsConnected)
            return;

        if (!IsCallOriginReachable(out var originFailure))
        {
            // 07-A8: a forced line carries no location (the territory is in debug lines only).
            CallTravelDebugLog.Write(
                _pi,
                _config,
                _log,
                $"owner Call blocked at origin | {originFailure}",
                force: true);
            return;
        }

        var world = player.CurrentWorld.ValueNullable;
        var worldId = player.CurrentWorld.RowId;
        // Still sent (07-A5): older dolls prefer it. New dolls resolve the name from WorldId.
        var worldName = world?.Name.ToString() ?? string.Empty;
        var requestId = Guid.NewGuid().ToString("N");
        var territoryId = _clientState.TerritoryType;

        var payload = new CallPayload
        {
            RequestId = requestId,
            From = _config.PairingKey,
            To = key,
            WorldId = worldId,
            WorldName = worldName,
            TerritoryId = territoryId,
            X = player.Position.X,
            Y = player.Position.Y,
            Z = player.Position.Z,
        };

        var housingDiag = new StringBuilder();
        if (HousingCallLocation.TryRead(territoryId, territoryRow: null, out var housing, _data, housingDiag))
        {
            // 07-A5 (D9): an indoor house with an unknown plot can't be reached; new dolls would refuse it anyway.
            if (!CallRequestRules.IsIndoorPlotKnown(housing.Indoor, housing.IsApartment, housing.Plot))
            {
                CallTravelDebugLog.Write(_pi, _config, _log, "owner Call blocked at origin | indoor house with an unknown plot", force: true);
                PluginChat.PrintError(_chat, CallIndoorPlotUnknownFailure);
                return;
            }

            payload.HousingCity = housing.City;
            payload.HousingWard = housing.Ward;
            payload.HousingDivision = housing.Division;
            payload.HousingPlot = housing.Plot;
            payload.HousingApartment = housing.Apartment;
            payload.HousingIsApartment = housing.IsApartment;
            payload.HousingIndoor = housing.Indoor;
            // Wards/subdivisions share TerritoryType — send the outdoor district id for travel.
            payload.TerritoryId = housing.OutdoorTerritoryId;
            CallTravelDebugLog.Write(
                _pi,
                _config,
                _log,
                $"owner Call snapshot OK → city={housing.City} w{housing.Ward} div{housing.Division} "
                + $"plot={housing.Plot} apt={housing.Apartment} indoor={housing.Indoor} "
                + $"outTerr={housing.OutdoorTerritoryId} pos=({payload.X:0.0},{payload.Y:0.0},{payload.Z:0.0}) "
                + $"world={payload.WorldId} | {housingDiag}");
        }
        else
        {
            CallTravelDebugLog.Write(
                _pi,
                _config,
                _log,
                $"owner Call snapshot FAIL → terr={territoryId} "
                + $"pos=({payload.X:0.0},{payload.Y:0.0},{payload.Z:0.0}) world={payload.WorldId} | {housingDiag}");
        }

        _pendingCalls.Add(requestId);

        await SendEnvelopeAsync(Envelope.Create(MessageTypes.Call, payload), CancellationToken.None)
            .ConfigureAwait(false);
    }

    public Task SendCallAckAsync(CallAckPayload payload) =>
        SendEnvelopeAsync(Envelope.Create(MessageTypes.CallAck, payload), CancellationToken.None);

    public Task SendCallResultAsync(CallResultPayload payload) =>
        SendEnvelopeAsync(Envelope.Create(MessageTypes.CallResult, payload), CancellationToken.None);

    /// <summary>
    /// Calls may only originate somewhere another player can actually reach. The condition checks
    /// cover duties/solo instances and transitions; the action-status check mirrors Lifestream's
    /// own teleport eligibility and catches special solo quest/society territories.
    /// </summary>
    private unsafe bool IsCallOriginReachable(out string failure)
    {
        if (_condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51])
        {
            failure = "between areas";
            return false;
        }

        if (_condition[ConditionFlag.BoundByDuty]
            || _condition[ConditionFlag.BoundByDuty56]
            || _condition[ConditionFlag.BoundByDuty95])
        {
            failure = "bound by duty/solo instance";
            return false;
        }

        var territories = _data.GetExcelSheet<TerritoryType>();
        if (territories is null || !territories.TryGetRow(_clientState.TerritoryType, out var territory))
        {
            failure = "unknown territory";
            return false;
        }

        // Sheet metadata catches duties and quest-battle/solo maps even when their live condition
        // flags are absent or delayed (notably isolated society quest locations).
        // 07-A8: the failure goes in a forced line, so it names no duty or quest (no row ids).
        if (territory.ContentFinderCondition.RowId != 0)
        {
            failure = "content instance";
            return false;
        }

        if (territory.QuestBattle.RowId != 0)
        {
            failure = "solo quest territory";
            return false;
        }

        var actionManager = ActionManager.Instance();
        if (actionManager is null)
        {
            failure = "ActionManager unavailable";
            return false;
        }

        var teleportStatus = actionManager->GetActionStatus(
            ActionType.Action,
            5,
            0xE0000000,
            true,
            true);
        if (teleportStatus != 0)
        {
            failure = $"Teleport unavailable (status={teleportStatus})";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    /// <summary>A silent cancel (the safeword, logout): never a Decline (07-B1).</summary>
    public void CancelActiveCall() => _callTravel?.CancelActiveCall();

    /// <summary>07-B1: <c>/windup stopcall</c>, which counts as a Decline; false when there's no Call.</summary>
    public bool RequestDeclineActiveCall() => _callTravel?.RequestDecline() == true;

    public bool HasDebugCallPoint => _callTravel?.HasDebugStoredPoint == true;

    /// <summary>07-A9: from ConfigWindow's Draw; the store runs on the next framework tick.</summary>
    public void RequestStoreDebugCallPoint() => _callTravel?.RequestStoreDebugPoint();

    /// <summary>07-A9: from ConfigWindow's Draw; the recall runs on the next framework tick.</summary>
    public void RequestRecallDebugCallPoint() => _callTravel?.RequestRecallDebugPoint();

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    /// <summary>Short offline hint for the config UI. Never includes URL or token.</summary>
    public string? LastStatus => _lastStatus;

    /// <summary>What to do after the relay rejected this character's session, or null when not blocked.</summary>
    public string? AuthBlockStatus => _authBlockStatus;

    /// <summary>
    /// True when the plugin has been unable to reach the relay long enough that
    /// unwound movement/teleport blocking should be suspended until reconnect.
    /// </summary>
    public bool ShouldSuspendMovementLocks =>
        _unreachableSinceUtc is { } since
        && DateTimeOffset.UtcNow - since >= MovementLockSafetyGrace;

    /// <summary>Latest identity sampled on the framework thread.</summary>
    public string? LocalIdentity => _cachedIdentity;

    /// <summary>Cached partner online status for the pairing UI.</summary>
    public PartnerPresence GetPartnerPresence(string partnerKey)
    {
        var key = PairingKeyUtil.Normalize(partnerKey);
        if (!PairingKeyUtil.IsValid(key))
            return PartnerPresence.Unknown;

        if (_config.IsDebugEnabled
            && string.Equals(key, PairingKeyUtil.Normalize(_config.PairingKey), StringComparison.Ordinal))
            return IsConnected ? PartnerPresence.Online : PartnerPresence.Offline;

        lock (_presenceLock)
            return _presenceByKey.TryGetValue(key, out var status) ? status : PartnerPresence.Unknown;
    }

    /// <summary>Latest owner-settings snapshot for a doll key (Owner tab).</summary>
    public OwnerSettingsSnapshot? GetOwnerSettings(string dollKey)
    {
        var key = PairingKeyUtil.Normalize(dollKey);
        if (!PairingKeyUtil.IsValid(key))
            return null;

        lock (_ownerSettingsLock)
            return _ownerSettingsByDollKey.TryGetValue(key, out var snap) ? CloneOwnerSettings(snap) : null;
    }

    /// <summary>Throttled presence refresh while the pairing tab is visible.</summary>
    public void EnsurePresenceFresh(string partnerKey)
    {
        var key = PairingKeyUtil.Normalize(partnerKey);
        if (!PairingKeyUtil.IsValid(key))
            return;

        if (_config.IsDebugEnabled
            && string.Equals(key, PairingKeyUtil.Normalize(_config.PairingKey), StringComparison.Ordinal))
            return;

        if (!IsConnected)
            return;

        var now = DateTime.UtcNow;
        lock (_presenceLock)
        {
            if (_presenceLastQueryUtc.TryGetValue(key, out var last) && now - last < PresenceThrottle)
                return;
            _presenceLastQueryUtc[key] = now;
        }

        Forget(RequestPresenceAsync(key), "presence query", background: true);
    }

    public async Task RequestPresenceAsync(string partnerKey)
    {
        var toKey = PairingKeyUtil.Normalize(partnerKey);
        if (!PairingKeyUtil.IsValid(toKey) || !IsConnected || !PairingKeyUtil.IsValid(_config.PairingKey))
            return;

        if (_config.IsDebugEnabled
            && string.Equals(toKey, PairingKeyUtil.Normalize(_config.PairingKey), StringComparison.Ordinal))
            return;

        var payload = new PresenceQueryPayload
        {
            RequestId = Guid.NewGuid().ToString("N"),
            From = _config.PairingKey,
            To = toKey,
        };
        await SendEnvelopeAsync(Envelope.Create(MessageTypes.PresenceQuery, payload), CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>Call from Framework.Update only — samples login/identity safely.</summary>
    public void Tick()
    {
        try
        {
            _loggedIn = _clientState.IsLoggedIn;
            // 06-A12: only a logout read here re-arms the profile-activated event (a read error below doesn't).
            if (!_loggedIn)
                _profileActivation.LoggedOut();
            _cachedIdentity = _loggedIn ? TryReadIdentity() : null;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "WindUpKey Tick: failed reading client state");
            _loggedIn = false;
            _characterReady = false;
            _cachedIdentity = null;
            _lastStatus = "Error reading character state (see dalamud.log).";
            UpdateUnreachableTracking();
            return;
        }

        var characterReady = false;
        if (_loggedIn)
        {
            try
            {
                // Skip ContentId / profile swap until LocalPlayer exists (zone load).
                if (_objectTable.LocalPlayer is not null)
                {
                    var contentId = TryReadLocalContentId();
                    if (contentId != 0)
                    {
                        var switched = _config.ActivateCharacter(contentId);
                        characterReady = true;

                        // Once per profile load or activation, before this profile connects or pushes anything.
                        if (!string.Equals(_stampsCheckedProfile, _config.ActiveContentId, StringComparison.Ordinal))
                        {
                            _stampsCheckedProfile = _config.ActiveContentId;
                            ResetFutureLocalSyncStamps();
                        }

                        // Orphan lock: settings locked with no owners (e.g. owner unpaired offline).
                        var unlocked = _config.UnlockOwnerSettingsIfNoOwners();
                        if (switched)
                        {
                            ClearAuthBlock();
                            ClearAllPresence();
                            ClearAllOwnerSettings();
                            _config.Save();
                            _log.Information("WindUpKey active character profile key={Key}", LogRedact.Key(PairingKeyUtil.FromContentIdHex(_config.ActiveContentId)));
                            if (IsConnected)
                                CloseSocket();
                        }
                        else if (unlocked)
                        {
                            _config.Save();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "WindUpKey Tick: ActivateCharacter failed");
            }
        }

        _characterReady = RelayConnectPolicy.IdentityCharacterReady(_loggedIn, characterReady, _characterReady);
        SyncPairingKeyFromContentId();
        UpdateUnreachableTracking();
        // 06-A12: once per login or plugin load, on the first tick that activated the logged-in character's profile.
        if (_profileActivation.TryRaise(characterReady))
            RaiseProfileActivated();

        if (Interlocked.Increment(ref _tickLogCounter) % 300 == 1)
        {
            RefreshUnlockedEmoteCache();
            _log.Debug(
                "WindUpKey Tick: running={Running} loggedIn={LoggedIn} identity={Identity} connected={Connected} status={Status}",
                Volatile.Read(ref _running) == 1,
                _loggedIn,
                LogRedact.Identity(_cachedIdentity),
                IsConnected,
                _lastStatus ?? "(null)");
        }
    }

    /// <summary>06-A2: framework thread, from <see cref="Tick"/>. A failing handler is logged, never thrown into the tick.</summary>
    private void RaiseProfileActivated()
    {
        try
        {
            ProfileActivated?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "WindUpKey profile-activated handler failed");
        }
    }

    /// <summary>
    /// Counts continuous "should be connected but isn't" time for the movement-lock safety fallback.
    /// Title screen / missing identity do not count — those are not a failed host connection.
    /// A relay auth block does count (its socket is closed), so a doll is never stuck on an auth problem.
    /// </summary>
    private void UpdateUnreachableTracking()
    {
        if (!_loggedIn || _cachedIdentity is null)
        {
            _unreachableSinceUtc = null;
            return;
        }

        if (IsConnected)
        {
            _unreachableSinceUtc = null;
            return;
        }

        _unreachableSinceUtc ??= DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Keeps <see cref="Configuration.PairingKey"/> aligned with the active ContentId.
    /// Rename and world transfer only update <see cref="Configuration.LastKnownIdentity"/> —
    /// ContentId (and thus the key) is unchanged.
    /// Safe to call on the framework thread (may Save).
    /// </summary>
    private void SyncPairingKeyFromContentId()
    {
        if (_cachedIdentity is not null)
            _config.LastKnownIdentity = _cachedIdentity;

        if (string.IsNullOrEmpty(_config.ActiveContentId))
            return;

        var derived = PairingKeyUtil.FromContentIdHex(_config.ActiveContentId);
        if (!PairingKeyUtil.IsValid(derived))
            return;

        if (string.Equals(_config.PairingKey, derived, StringComparison.Ordinal))
            return;

        _config.PairingKey = derived;
        _config.Save();
        _log.Information("WindUpKey pairing key synced from ContentId");

        // Re-register if we connected before the key was available / changed.
        if (IsConnected)
            CloseSocket();
    }

    public void Start()
    {
        // 04-D10: nothing queued on the framework before an unload (a self-test restore, say) may start a connect loop
        // inside the unloaded plugin.
        if (_disposed)
        {
            _log.Debug("WindUpKey relay Start() ignored: disposed");
            return;
        }

        _log.Information("WindUpKey relay Start()");
        Stop();

        _lastStatus = "Starting…";
        ClearAuthBlock();
        // A login or Reconnect retries the identity file now, not after its retry delay.
        _identityStore.ResetRetryDelay();
        _backoff.Reset();
        _loopCts = new CancellationTokenSource();
        var ct = _loopCts.Token;
        var generation = Interlocked.Increment(ref _loopGeneration);
        Interlocked.Exchange(ref _running, 1);

        _loopTask = Task.Run(async () =>
        {
            _log.Debug("WindUpKey relay background loop entered");
            try
            {
                await RunLoopAsync(generation, ct).ConfigureAwait(false);
                _log.Debug("WindUpKey relay background loop exited normally");
            }
            catch (OperationCanceledException)
            {
                // Start() cancels any previous loop via Stop(); expected, not a crash.
                _log.Debug("WindUpKey relay background loop canceled");
            }
            catch (Exception ex)
            {
                _lastStatus = "Connection loop crashed (see dalamud.log).";
                _log.Error(ex, "WindUpKey relay background loop crashed");
            }
            finally
            {
                if (Volatile.Read(ref _loopGeneration) == generation)
                    Interlocked.Exchange(ref _running, 0);
            }
        });
    }

    public void Stop()
    {
        _log.Debug("WindUpKey relay Stop()");
        Interlocked.Increment(ref _loopGeneration);
        try
        {
            _loopCts?.Cancel();
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "WindUpKey relay Stop cancel");
        }

        _loopCts = null;
        _loopTask = null;
        CloseSocket();
        Interlocked.Exchange(ref _running, 0);
        _lastStatus = "Stopped.";
    }

    public async Task SendWindAsync(string targetIdentity, double hours)
    {
        if (_cachedIdentity is null)
        {
            _notifier.NotifyWinderError("Not logged in.");
            return;
        }

        if (_config.IsDebugEnabled)
        {
            var self = string.Equals(
                PlayerIdentity.Normalize(targetIdentity),
                PlayerIdentity.Normalize(_cachedIdentity),
                StringComparison.OrdinalIgnoreCase);
            if (self)
            {
                await SendWindByKeyAsync(_config.PairingKey, hours).ConfigureAwait(false);
                return;
            }
        }

        var pair = _config.FindPair(targetIdentity);
        if (pair is null || !PairingKeyUtil.IsValid(pair.PartnerKey))
        {
            _notifier.NotifyWinderError(GenericWindFailure);
            return;
        }

        RememberPartnerIdentity(pair, targetIdentity);
        await SendWindByKeyAsync(pair.PartnerKey, hours).ConfigureAwait(false);
    }

    public async Task SendWindByKeyAsync(string partnerKey, double hours)
    {
        if (_cachedIdentity is null)
        {
            _notifier.NotifyWinderError("Not logged in.");
            return;
        }

        if (!IsConnected)
        {
            _notifier.NotifyWinderError("Not connected yet. Try again in a moment.");
            return;
        }

        if (!PairingKeyUtil.IsValid(_config.PairingKey))
        {
            _notifier.NotifyWinderError(GenericWindFailure);
            return;
        }

        var toKey = PairingKeyUtil.Normalize(partnerKey);
        if (!PairingKeyUtil.IsValid(toKey))
        {
            _notifier.NotifyWinderError(GenericWindFailure);
            return;
        }

        var payload = new WindPayload
        {
            RequestId = Guid.NewGuid().ToString("N"),
            From = _config.PairingKey,
            To = toKey,
            Hours = hours,
        };

        RememberPendingWind(payload.RequestId, hours);
        // 09-K5: a wind that didn't go out (no open socket, over the limit, or a throw, which still reaches Forget's
        // log) gets no reply, so its pending entry goes and the winder is told, on the framework thread.
        var sent = false;
        try
        {
            sent = await SendEnvelopeAsync(Envelope.Create(MessageTypes.Wind, payload), CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            if (!sent)
            {
                Forget(_framework.RunOnTick(() =>
                {
                    // 08-P2 (K3a): nothing after an unload.
                    if (_disposed)
                        return;

                    TryTakePendingWind(payload.RequestId, out _);
                    _notifier.NotifyWinderError("Not connected yet. Try again in a moment.");
                }), "wind send result", background: true);
            }
        }
    }

    public TimeSpan WindRequestCooldownRemaining
    {
        get
        {
            if (_config.LastWindRequestUtc is not { } last)
                return TimeSpan.Zero;
            var remaining = WindRequestCooldown - (DateTimeOffset.UtcNow - last);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    public async Task SendWindRequestAsync(string partnerKey)
    {
        if (!_config.IsDoll || _cachedIdentity is null)
        {
            _notifier.NotifyWinderError("Only a logged-in doll can request winding.");
            return;
        }

        if (!IsConnected)
        {
            _notifier.NotifyWinderError("Not connected yet. Try again in a moment.");
            return;
        }

        if (WindRequestCooldownRemaining > TimeSpan.Zero)
        {
            _notifier.NotifyWinderError("You may request winding only once per hour.");
            return;
        }

        var toKey = PairingKeyUtil.Normalize(partnerKey);
        if (!PairingKeyUtil.IsValid(toKey) || _config.FindPairByKey(toKey) is null)
        {
            _notifier.NotifyWinderError("Winding requests can only be sent to a paired partner.");
            return;
        }

        var maxSeconds = TimeSpan.FromHours(Math.Max(0.01, _config.MaxWindHours)).TotalSeconds;
        var percent = (int)Math.Round(
            Math.Clamp(_timer.RemainingForWinder().TotalSeconds / maxSeconds * 100.0, 0.0, 100.0),
            MidpointRounding.AwayFromZero);
        var payload = new WindRequestPayload
        {
            RequestId = Guid.NewGuid().ToString("N"),
            From = _config.PairingKey,
            To = toKey,
            PercentRemaining = percent,
        };

        // 09-K4: the cooldown still starts before the send, so a double click can't send twice. A send that doesn't go
        // out gives it back below, only while it is still this stamp on this character.
        var previous = _config.LastWindRequestUtc;
        var stamp = DateTimeOffset.UtcNow;
        var contentId = _config.ActiveContentId;
        _config.LastWindRequestUtc = stamp;
        _config.Save();
        lock (_pendingWindLock)
        {
            _pendingWindRequestIds.Clear();
            _pendingWindRequestIds.Add(payload.RequestId);
        }

        var sent = false;
        try
        {
            sent = await SendEnvelopeAsync(Envelope.Create(MessageTypes.WindRequest, payload), CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            // 09-K4: chat and config belong to the framework thread. A throw counts as unsent and still reaches
            // Forget's log.
            Forget(_framework.RunOnTick(() =>
            {
                // 08-P2 (K3a): nothing after an unload.
                if (_disposed)
                    return;

                if (sent)
                {
                    PluginChat.Print(_chat, "Your request for additional winding was sent.", PluginChat.Yellow);
                    return;
                }

                // Nothing went out, so no reply comes for it. After a character switch, or under a newer stamp, the
                // cooldown now set is left alone. (Qualified: this class's own WindRequestCooldown is the hour.)
                lock (_pendingWindLock)
                    _pendingWindRequestIds.Remove(payload.RequestId);
                if (Services.WindRequestCooldown.ShouldRollBack(
                        _config.LastWindRequestUtc, _config.ActiveContentId, stamp, contentId))
                {
                    _config.LastWindRequestUtc = previous;
                    _config.Save();
                }

                _notifier.NotifyWinderError("Unable to send that winding request.");
            }), "wind request result", background: true);
        }
    }

    public async Task SendUnwindAsync(string targetIdentity)
    {
        if (_cachedIdentity is null)
        {
            _notifier.NotifyWinderError("Not logged in.");
            return;
        }

        if (_config.IsDebugEnabled)
        {
            var self = string.Equals(
                PlayerIdentity.Normalize(targetIdentity),
                PlayerIdentity.Normalize(_cachedIdentity),
                StringComparison.OrdinalIgnoreCase);
            if (self)
            {
                await SendUnwindByKeyAsync(_config.PairingKey).ConfigureAwait(false);
                return;
            }
        }

        var pair = _config.FindPair(targetIdentity);
        if (pair is null || !PairingKeyUtil.IsValid(pair.PartnerKey))
        {
            _notifier.NotifyWinderError(GenericWindFailure);
            return;
        }

        RememberPartnerIdentity(pair, targetIdentity);
        await SendUnwindByKeyAsync(pair.PartnerKey).ConfigureAwait(false);
    }

    public async Task SendUnwindByKeyAsync(string partnerKey)
    {
        if (_cachedIdentity is null)
        {
            _notifier.NotifyWinderError("Not logged in.");
            return;
        }

        if (!IsConnected)
        {
            _notifier.NotifyWinderError("Not connected yet. Try again in a moment.");
            return;
        }

        if (!PairingKeyUtil.IsValid(_config.PairingKey))
        {
            _notifier.NotifyWinderError(GenericWindFailure);
            return;
        }

        var toKey = PairingKeyUtil.Normalize(partnerKey);
        if (!PairingKeyUtil.IsValid(toKey))
        {
            _notifier.NotifyWinderError(GenericWindFailure);
            return;
        }

        var payload = new UnwindPayload
        {
            RequestId = Guid.NewGuid().ToString("N"),
            From = _config.PairingKey,
            To = toKey,
        };

        // 09-K5: an unwind that didn't go out (no open socket, over the limit, or a throw, which still reaches Forget's
        // log) is told to the winder, on the framework thread.
        var sent = false;
        try
        {
            sent = await SendEnvelopeAsync(Envelope.Create(MessageTypes.Unwind, payload), CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            if (!sent)
            {
                Forget(_framework.RunOnTick(() =>
                {
                    // 08-P2 (K3a): nothing after an unload.
                    if (_disposed)
                        return;

                    _notifier.NotifyWinderError("Not connected yet. Try again in a moment.");
                }), "unwind send result", background: true);
            }
        }
    }

    public async Task SubmitPairKeyAsync(string theirKeyRaw)
    {
        var theirKey = PairingKeyUtil.Normalize(theirKeyRaw);
        if (!PairingKeyUtil.IsValid(theirKey))
        {
            _notifier.NotifyWinderError(GenericPairFailure);
            return;
        }

        if (string.Equals(theirKey, _config.PairingKey, StringComparison.Ordinal))
        {
            if (_config.IsDebugEnabled)
            {
                // Local self-pair for debug — no relay handshake (relay rejects MyKey == TheirKey).
                if (_config.FindPairByKey(theirKey) is null)
                {
                    _config.PairedPartners.Add(new PairedPartner
                    {
                        Identity = string.Empty,
                        PartnerKey = theirKey,
                        CanWindMe = false,
                    });
                    _config.Save();
                    _log.Information("WindUpKey paired with key={Key}", LogRedact.Key(theirKey));
                }

                return;
            }

            _notifier.NotifyWinderError(GenericPairFailure);
            return;
        }

        if (!IsConnected)
        {
            _notifier.NotifyWinderError("Not connected yet. Try again in a moment.");
            return;
        }

        // Pairing again cancels an unpair still waiting to be sent.
        var changed = PairRemovalQueue.Cancel(_config.PendingPairRemovals, theirKey);
        _unpairedThisConnection.Remove(theirKey);
        if (!_config.PendingPartnerKeys.Contains(theirKey, StringComparer.Ordinal))
        {
            _config.PendingPartnerKeys.Add(theirKey);
            changed = true;
        }

        if (changed)
            _config.Save();

        var payload = new PairSubmitPayload
        {
            RequestId = Guid.NewGuid().ToString("N"),
            MyKey = _config.PairingKey,
            TheirKey = theirKey,
        };
        await SendEnvelopeAsync(Envelope.Create(MessageTypes.PairSubmit, payload), CancellationToken.None);
    }

    public async Task CancelPendingPairAsync(string partnerKeyRaw)
    {
        var partnerKey = PairingKeyUtil.Normalize(partnerKeyRaw);
        if (!PairingKeyUtil.IsValid(partnerKey)
            || !_config.PendingPartnerKeys.Contains(partnerKey, StringComparer.Ordinal))
            return;

        if (!IsConnected)
        {
            _notifier.NotifyWinderError("Not connected yet. Try again in a moment.");
            return;
        }

        try
        {
            await SendEnvelopeAsync(Envelope.Create(MessageTypes.PairCancel, new PairCancelPayload
            {
                PeerKey = partnerKey,
            }), CancellationToken.None).ConfigureAwait(false);

            await _framework.RunOnTick(() =>
            {
                // 08-P2 (K3a): a tick queued before an unload never touches the config of the unloaded plugin.
                if (_disposed)
                    return;

                if (_config.PendingPartnerKeys.RemoveAll(k =>
                        string.Equals(PairingKeyUtil.Normalize(k), partnerKey, StringComparison.Ordinal)) > 0)
                {
                    _config.Save();
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "WindUpKey failed to cancel pending pair");
            _notifier.NotifyWinderError("Unable to cancel that pending pairing request.");
        }
    }

    public async Task UnpairByKeyAsync(string partnerKey)
    {
        var peerKey = PairingKeyUtil.Normalize(partnerKey);
        if (!PairingKeyUtil.IsValid(peerKey))
            return;

        // Framework thread (the config window). 02-D30: an owner unpairing its doll also stops owning it here, live or
        // queued, because the relay drops the doll's reply ownerRevoked once the pair is gone.
        var local = RelaySyncRules.RemovePartnerLocally(_config.PairedPartners, _config.OwnedDolls, peerKey);
        _config.UnlockOwnerSettingsIfNoOwners();

        // Kept for this character until the relay has it: sent now when connected, otherwise on the next connect.
        // Self-pair is local only — nothing to notify on the relay (not queued).
        var removal = PairRemovalQueue.Queue(_config.PendingPairRemovals, peerKey, _config.PairingKey, local.WasOwner);
        _config.Save();
        ClearPresence(peerKey);
        ClearOwnerSettings(peerKey);

        if (removal is null || !IsConnected)
            return;

        await SendQueuedPairRemovalAsync(removal, peerKey).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a queued unpair of <paramref name="peerKey"/> on this connection (its <c>ownerRevoked</c> first, when it
    /// has one), and completes it once sent on a connection whose register was accepted, for the same character.
    /// Otherwise it stays queued and is sent again on the next connect. Started on the framework thread, which reads
    /// the gate and the character before the first await. Used by <see cref="UnpairByKeyAsync"/>; a presence reply never
    /// queues one (08-D19).
    /// </summary>
    private async Task SendQueuedPairRemovalAsync(PendingPairRemoval removal, string peerKey)
    {
        // Sent before this connection's register was accepted, a refusing host would ignore it: then it stays queued.
        var accepted = _registerAccepted;
        var contentId = _config.ActiveContentId;
        try
        {
            if (removal.RevokeOwner && !await SendOwnerRevokedAsync(peerKey).ConfigureAwait(false))
                return;

            var sent = await SendEnvelopeAsync(Envelope.Create(MessageTypes.PairRemove, new PairRemovePayload
            {
                PeerKey = peerKey,
            }), CancellationToken.None).ConfigureAwait(false);
            if (!sent || !accepted)
                return;

            await _framework.RunOnTick(() =>
            {
                // 08-P2 (K3a): nothing after an unload; the removal stays queued and is sent on the next connect.
                if (_disposed)
                    return;

                // After a character switch it stays queued on that character, and is sent again on its next connect.
                if (string.Equals(_config.ActiveContentId, contentId, StringComparison.Ordinal)
                    && PairRemovalQueue.Complete(_config.PendingPairRemovals, [removal]))
                {
                    _config.Save();
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "WindUpKey failed to send an unpair; it is sent again on the next connect");
        }
    }

    /// <summary>Doll designates a paired partner as an owner (idempotent).</summary>
    public async Task GrantOwnerAsync(string partnerKey)
    {
        if (!_config.IsDoll)
            return;

        var peerKey = PairingKeyUtil.Normalize(partnerKey);
        var pair = _config.FindPairByKey(peerKey);
        if (pair is null)
            return;

        if (!pair.IsOwner)
        {
            pair.IsOwner = true;
            pair.ConsentUpdatedUtc = DateTimeOffset.UtcNow;
            _config.Save();
            QueueDollStatePush();
        }

        var isSelf = string.Equals(peerKey, _config.PairingKey, StringComparison.Ordinal);
        if (isSelf)
        {
            // Debug self-pair: no relay round-trip — mirror ownerGrant locally so the Owner tab appears.
            if (_config.IsDebugEnabled)
                ApplyLocalOwnerGrant(peerKey, LocalOwnerIdentityLabel());
            return;
        }

        if (!IsConnected)
            return;

        await SendEnvelopeAsync(Envelope.Create(MessageTypes.OwnerGrant, new OwnerGrantPayload
        {
            From = _config.PairingKey,
            To = peerKey,
            Identity = LocalOwnerIdentityLabel(),
        }), CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Debug self-test: if this character owns itself but <see cref="Configuration.OwnedDolls"/> is empty
    /// (e.g. grant before this fix), repair so the Owner tab shows.
    /// </summary>
    public void SyncDebugSelfOwnership()
    {
        if (!_config.IsDebugEnabled)
            return;

        var selfKey = PairingKeyUtil.Normalize(_config.PairingKey);
        if (!PairingKeyUtil.IsValid(selfKey))
            return;

        var pair = _config.FindPairByKey(selfKey);
        if (pair is not { IsOwner: true })
            return;

        if (_config.FindOwnedDoll(selfKey) is not null)
            return;

        ApplyLocalOwnerGrant(selfKey, LocalOwnerIdentityLabel());
    }

    /// <summary>Clear all owners, unlock owner settings, and notify former owners.</summary>
    public async Task ClearAllOwnersAsync()
    {
        var ownerKeys = _config.PairedPartners
            .Where(p => p.IsOwner)
            .Select(p => PairingKeyUtil.Normalize(p.PartnerKey))
            .Where(PairingKeyUtil.IsValid)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var partner in _config.PairedPartners)
        {
            if (!partner.IsOwner)
                continue;
            partner.IsOwner = false;
            partner.ConsentUpdatedUtc = DateTimeOffset.UtcNow;
        }

        _config.OwnerSettingsLocked = false;

        // Self-owner lives on this same client — clear OwnedDolls locally (no useful relay revoke).
        var selfKey = PairingKeyUtil.Normalize(_config.PairingKey);
        if (PairingKeyUtil.IsValid(selfKey) && ownerKeys.Contains(selfKey, StringComparer.Ordinal))
        {
            _config.RemoveOwnedDoll(selfKey);
            ClearOwnerSettings(selfKey);
        }

        _config.Save();
        QueueDollStatePush();

        if (!IsConnected)
            return;

        foreach (var key in ownerKeys)
        {
            if (string.Equals(key, selfKey, StringComparison.Ordinal))
                continue;
            await SendOwnerRevokedAsync(key).ConfigureAwait(false);
        }
    }

    public Task QueryOwnerSettingsAsync(string dollKey) => SendOwnerSettingsQueryAsync(dollKey, Guid.NewGuid().ToString("N"));

    /// <summary>
    /// 08-T6 (D10): the re-query after a <c>safeword_paused</c> refusal, started on the framework thread. 08-D17: its
    /// RequestId is recorded before the send, so only its own result keeps the "paused" message; when it isn't sent (no
    /// socket, a message the relay wouldn't take, or a throw) <see cref="OwnerSettingsSnapshot.KeepErrorOnNextResult"/>
    /// is cleared, so it can't stick and keep the next refusal from asking again.
    /// </summary>
    private async Task ReQueryOwnerSettingsAsync(string dollKey)
    {
        var requestId = Guid.NewGuid().ToString("N");
        SetOwnerSettingsReQuery(dollKey, requestId);
        var sent = false;
        try
        {
            sent = await SendOwnerSettingsQueryAsync(dollKey, requestId).ConfigureAwait(false);
        }
        finally
        {
            if (!sent)
                ClearOwnerSettingsReQuery(dollKey, requestId);
        }
    }

    /// <summary>
    /// Asks the doll for her settings under <paramref name="requestId"/>. True only when the query went out to the relay;
    /// false for an invalid key, the debug self-owner (answered locally), no connection, or a send that didn't go out.
    /// </summary>
    private async Task<bool> SendOwnerSettingsQueryAsync(string dollKey, string requestId)
    {
        var key = PairingKeyUtil.Normalize(dollKey);
        if (!PairingKeyUtil.IsValid(key))
            return false;

        EnsureOwnerSettingsSlot(key);

        // Debug self-owner: answer from local config (relay self-route is unreliable for UI testing).
        if (_config.IsDebugEnabled
            && string.Equals(key, PairingKeyUtil.Normalize(_config.PairingKey), StringComparison.Ordinal))
        {
            ApplyLocalOwnerSettingsSnapshot(key);
            return false;
        }

        if (!IsConnected)
        {
            SetOwnerSettingsError(key, "Not connected yet. Try again in a moment.");
            return false;
        }

        RememberPendingOwnerRequest(requestId, key);

        return await SendEnvelopeAsync(Envelope.Create(MessageTypes.OwnerSettingsQuery, new OwnerSettingsQueryPayload
        {
            RequestId = requestId,
            From = _config.PairingKey,
            To = key,
        }), CancellationToken.None).ConfigureAwait(false);
    }

    public async Task UpdateOwnerSettingsAsync(
        string dollKey,
        double? maxWindHours = null,
        bool? autoGroundSit = null,
        ushort? lockEmoteId = null,
        bool? settingsLocked = null,
        bool notify = true)
    {
        var key = PairingKeyUtil.Normalize(dollKey);
        if (!PairingKeyUtil.IsValid(key))
            return;

        EnsureOwnerSettingsSlot(key);

        if (_config.IsDebugEnabled
            && string.Equals(key, PairingKeyUtil.Normalize(_config.PairingKey), StringComparison.Ordinal))
        {
            // 06-B3: the pause refuses her own debug settings change too, as it does self-unwind and self-Call.
            if (_timer.IsRemoteControlPaused)
            {
                ApplyLocalOwnerSettingsSnapshot(key);
                RestoreOwnerSettingsAfterRefusal(key, SafewordPausedFailure);
                _notifier.NotifyWinderError(SafewordPausedFailure);
                return;
            }

            ApplyLocalOwnerSettingsUpdate(maxWindHours, autoGroundSit, lockEmoteId, settingsLocked);
            ApplyLocalOwnerSettingsSnapshot(key);
            if (notify)
                PluginChat.Print(_chat, $"Doll settings updated for {GetPairMessageLabel(key)}.", PluginChat.Green);
            return;
        }

        if (!IsConnected)
        {
            SetOwnerSettingsError(key, "Not connected yet. Try again in a moment.");
            return;
        }

        var requestId = Guid.NewGuid().ToString("N");
        RememberPendingOwnerRequest(requestId, key, notify);

        await SendEnvelopeAsync(Envelope.Create(MessageTypes.OwnerSettingsUpdate, new OwnerSettingsUpdatePayload
        {
            RequestId = requestId,
            From = _config.PairingKey,
            To = key,
            MaxWindHours = maxWindHours,
            AutoGroundSit = autoGroundSit,
            LockEmoteId = lockEmoteId,
            SettingsLocked = settingsLocked,
        }), CancellationToken.None).ConfigureAwait(false);
    }

    private string? LocalOwnerIdentityLabel() =>
        string.IsNullOrWhiteSpace(_config.LastKnownIdentity)
            ? _cachedIdentity
            : _config.LastKnownIdentity;

    private void ApplyLocalOwnerGrant(string dollKey, string? identity)
    {
        // Presence sync resends ownerGrant while both sides are online; only announce first grant.
        var isNew = _config.FindOwnedDoll(dollKey) is null;
        _config.UpsertOwnedDoll(dollKey, identity);
        _config.Save();
        if (!isNew)
            return;

        PluginChat.Print(
            _chat,
            $"You were designated as an owner by {GetPairMessageLabel(dollKey)}.",
            PluginChat.Green);
    }

    private void ApplyLocalOwnerSettingsUpdate(
        double? maxWindHours,
        bool? autoGroundSit,
        ushort? lockEmoteId,
        bool? settingsLocked)
    {
        if (maxWindHours is { } hours)
            _config.MaxWindHours = Math.Clamp(Math.Round(hours), 1, 168);

        if (autoGroundSit is { } autoSit)
            _config.AutoGroundSit = autoSit;

        // 06-C6: an excluded emote is applied as Ground Sit, as from a remote owner.
        if (lockEmoteId is { } emoteId)
            _config.LockEmoteId = LockEmoteRule.FromRemote(emoteId, _commands.IsAllowedLockEmote(emoteId));

        if (settingsLocked is { } locked)
            _config.OwnerSettingsLocked = locked;

        _config.Save();
    }

    private void ApplyLocalOwnerSettingsSnapshot(string dollKey)
    {
        RefreshUnlockedEmoteCache();
        var emotes = SnapshotUnlockedEmotes()
            .Select(e => (e.Id, e.Name ?? string.Empty))
            .ToList();

        lock (_ownerSettingsLock)
        {
            _ownerSettingsByDollKey[dollKey] = new OwnerSettingsSnapshot
            {
                DollKey = dollKey,
                MaxWindHours = _config.MaxWindHours,
                AutoGroundSit = _config.AutoGroundSit,
                LockEmoteId = _config.EffectiveLockEmoteId,
                SettingsLocked = _config.OwnerSettingsLocked,
                Emotes = emotes,
                CanCall = true,
                TravelReady = TravelPluginsReady(),
                LastError = null,
                HasData = true,
            };
        }
    }

    /// <summary>
    /// 08-P1 (K3d, D8): a task nobody awaits, started on the caller's thread. A fault is logged at Warning for a player's
    /// action, or at Debug for <paramref name="background"/> work (presence, the doll-state push, revoke), never left
    /// unobserved. <paramref name="what"/> is a constant: never a key or anything else from a message (04-D10).
    /// </summary>
    internal void Forget(Task task, string what, bool background = false) =>
        FireAndForget.Run(task, ex =>
        {
            if (background)
                _log.Debug(ex, "WindUpKey {What} failed", what);
            else
                _log.Warning(ex, "WindUpKey {What} failed", what);
        });

    private Task<bool> SendOwnerRevokedAsync(string ownerKey) =>
        SendEnvelopeAsync(Envelope.Create(MessageTypes.OwnerRevoked, new OwnerRevokedPayload
        {
            From = _config.PairingKey,
            To = ownerKey,
        }), CancellationToken.None);

    private string? TryReadIdentity()
    {
        var player = _objectTable.LocalPlayer;
        if (player is null)
            return null;

        string? world = null;
        try
        {
            world = player.HomeWorld.ValueNullable?.Name.ToString();
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "WindUpKey identity: HomeWorld read failed");
        }

        if (string.IsNullOrEmpty(world))
            return null;

        var name = player.Name.TextValue;
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return PlayerIdentity.Format(name, world);
    }

    private static unsafe ulong TryReadLocalContentId()
    {
        var info = InfoModule.Instance();
        return info is null ? 0UL : info->GetLocalContentId();
    }

    private async Task RunLoopAsync(int generation, CancellationToken ct)
    {
        _log.Debug("WindUpKey RunLoopAsync begin");
        while (!ct.IsCancellationRequested)
        {
            if (!_loggedIn)
            {
                _lastStatus = "Log into the game to connect.";
                await Task.Delay(1000, ct).ConfigureAwait(false);
                continue;
            }

            var identity = _cachedIdentity;
            if (identity is null)
            {
                _lastStatus = "Waiting for character data…";
                await Task.Delay(1000, ct).ConfigureAwait(false);
                continue;
            }

#if WINDUP_TESTING
            if (_selfTestOutageHold)
            {
                // 04-D7: the self-test's outage, in memory only. Not connecting counts as unreachable, so the 60 s safety
                // rule suspends the locks as for a real outage.
                _lastStatus = "Self-test outage: not connecting…";
                await Task.Delay(1000, ct).ConfigureAwait(false);
                continue;
            }
#endif

            if (_connectPolicy.Block == RelayAuthBlock.Full)
            {
                // Every host rejected this identity, or the session was replaced: wait for Start(), a character
                // switch, an import or a revert.
                _lastStatus = _authBlockStatus ?? "Waiting for Reconnect…";
                await Task.Delay(1000, ct).ConfigureAwait(false);
                continue;
            }

            // A partial block keeps probing the hosts that were unreachable (the policy skips the ones that rejected).
            var epoch = _connectPolicy.Epoch;
            try
            {
                _lastStatus = _authBlockStatus ?? _connectPolicy.CurrentCloseStatus ?? "Connecting…";
                _log.Information("WindUpKey loop: connecting as {Identity}", LogRedact.Identity(identity));
                var connection = await ConnectAndRegisterAsync(generation, epoch, ct).ConfigureAwait(false);
                _lastStatus = null;
                var rejectedCode = await ReceiveLoopAsync(connection.Socket, epoch, connection.Profile, ct).ConfigureAwait(false);
                if (rejectedCode is not null)
                {
                    // auth_failed/auth_required is per host (each has its own accounts): try the next one now,
                    // and block only once every reachable host has rejected this identity.
                    CloseSocketForGeneration(generation);
                    var verdict = await _framework.RunOnTick(
                            () => ApplyHostVerdict(_connectPolicy.Rejected(epoch, connection.Url, rejectedCode, connection.Candidates)),
                            cancellationToken: ct)
                        .ConfigureAwait(false);
                    if (verdict == RelayHostVerdict.TryNextHost)
                    {
                        _log.Information(
                            "WindUpKey relay host={Host} rejected the relay identity ({Code}); trying the next host",
                            SafeHost(connection.Url),
                            rejectedCode);
                    }

                    continue;
                }

                // Rejections were already forgotten when the register was accepted (pairSync), however this ends.
                _lastStatus = _authBlockStatus ?? _connectPolicy.CurrentCloseStatus ?? "Disconnected. Reconnecting…";
            }
            // Only our own cancel stops the loop. A keep-alive abort (05-D3) is an OperationCanceledException with ct not
            // cancelled: it falls through to the general catch, an ordinary disconnect that reconnects on the backoff.
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (StaleConnectAttemptException)
            {
                // Started before a character switch (or login, import, revert): retry now for the current one.
                CloseSocketForGeneration(generation);
                continue;
            }
            catch (RelayConnectWaitException wait)
            {
                // Not a host problem (identity file busy or from a newer build): never an auth block.
                _lastStatus = wait.Status;
                _log.Debug("WindUpKey not connecting: {Reason}", wait.Step);
            }
            catch (NoRelayHostReachableException ex)
            {
                // Every host that answered rejected the identity and the rest are down: a partial block, which
                // keeps probing those on the normal backoff below.
                var verdict = await _framework.RunOnTick(
                        () => ApplyHostVerdict(_connectPolicy.NoHostReachable(epoch)),
                        cancellationToken: ct)
                    .ConfigureAwait(false);
                _connectPolicy.ClearCloseStatus();
                _lastStatus = _authBlockStatus ?? "Cannot reach the relay. Check your connection; the relay may be down.";
                if (verdict == RelayHostVerdict.Blocked)
                    _log.Information("WindUpKey no other relay host reachable after an identity rejection");
                else
                    _log.Warning(ex, "WindUpKey relay connection issue");
            }
            catch (Exception ex)
            {
                _connectPolicy.ClearCloseStatus();
                _lastStatus = _authBlockStatus ?? "Cannot reach the relay. Check your connection; the relay may be down.";
                _log.Warning(ex, "WindUpKey relay connection issue");
            }

            CloseSocketForGeneration(generation);
            try
            {
                await Task.Delay(_backoff.Next(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _log.Debug("WindUpKey RunLoopAsync end");
    }

    /// <summary>
    /// An open, registered relay connection, the hosts that were candidates for it, and the profile (ContentId hex) it
    /// registered for, read in the same tick as its key and secret.
    /// </summary>
    private sealed record RelayConnection(ClientWebSocket Socket, string Url, IReadOnlyList<string> Candidates, string Profile);

    /// <summary>No candidate host (other than ones that rejected the identity this cycle) could be opened.</summary>
    private sealed class NoRelayHostReachableException(Exception? inner)
        : Exception("Unable to reach any relay host.", inner);

    /// <summary>The connect cycle was reset (character switch, login, import, revert) while this attempt was being prepared.</summary>
    private sealed class StaleConnectAttemptException()
        : Exception("The connect attempt is out of date.");

    /// <summary>Not connecting this time (see <see cref="Step"/>); retried on the normal backoff, never an auth block.</summary>
    private sealed class RelayConnectWaitException(RelayConnectStep step, string status)
        : Exception(status)
    {
        public RelayConnectStep Step { get; } = step;

        public string Status { get; } = status;
    }

    private async Task<RelayConnection> ConnectAndRegisterAsync(
        int generation,
        int epoch,
        CancellationToken ct)
    {
        CloseSocketForGeneration(generation);

        string? relaySecret = null;
        var pairingKey = string.Empty;
        var profile = string.Empty;
        var stale = false;
        var step = RelayConnectStep.WaitForCharacter;
        // What the post-register sends need, read in the same tick as the key and secret (config is framework-thread only).
        string[] pendingKeys = [];
        PendingPairRemoval[] removals = [];
        (string Key, bool IsOwner)[] partners = [];
        var isDoll = false;
        string? ownerIdentity = null;
        _registerAccepted = false;
#if WINDUP_TESTING
        _lastPeerKeys = null;
        _selfTestRegisteredKey = null;
#endif
        Volatile.Write(ref _removalsAwaitingSync, null);

        // Keep even the connection-preparation config writes on the framework thread.
        await _framework.RunOnTick(() =>
        {
            // A character switch (or login, import, revert) since this attempt started: start over.
            stale = _connectPolicy.Epoch != epoch;
            if (stale)
                return;

            _config.ApplyRelayDefaults();
            var identity = _cachedIdentity;
            if (identity is not null)
                _config.LastKnownIdentity = identity;

            var hasCharacter = !string.IsNullOrEmpty(_config.ActiveContentId);
            // Read from disk (under the lock) on every connect, so a value another client saved or imported is used.
            relaySecret = hasCharacter ? EnsureRelaySecret() : null;
            step = _connectPolicy.BeforeConnect(hasCharacter, relaySecret is not null, _identityStore.Status);
            if (!hasCharacter)
                return;

            var derived = PairingKeyUtil.FromContentIdHex(_config.ActiveContentId);
            if (!PairingKeyUtil.IsValid(derived))
                return;

            // The key registered is the one derived in the same tick as the secret, so they always belong to the
            // same character even if the player switches while the socket opens.
            pairingKey = derived;
            profile = _config.ActiveContentId;
            pendingKeys = _config.PendingPartnerKeys.ToArray();
            removals = PairRemovalQueue.ToSend(_config.PendingPairRemovals, derived);
            _unpairedThisConnection.Clear();
            partners = _config.PairedPartners.Where(p => p is not null).Select(p => (p.PartnerKey, p.IsOwner)).ToArray();
            isDoll = _config.IsDoll;
            ownerIdentity = LocalOwnerIdentityLabel();
            if (string.Equals(_config.PairingKey, derived, StringComparison.Ordinal))
                return;

            _config.PairingKey = derived;
            _config.Save();
        }, cancellationToken: ct).ConfigureAwait(false);

        if (stale)
            throw new StaleConnectAttemptException();

        // Never register without a secret: on a claimed key that is refused, and would block with the wrong advice.
        if (step != RelayConnectStep.Register)
            throw new RelayConnectWaitException(step, ConnectWaitStatus(step));

        if (!PairingKeyUtil.IsValid(pairingKey))
            throw new InvalidOperationException("Pairing key unavailable before register.");

        var candidates = RelayDefaults.OrderedRelayUrls(_config.LastSuccessfulRelayUrl).ToArray();
        if (candidates.Length == 0)
            throw new InvalidOperationException("No relay hosts configured.");

        Exception? lastError = null;
        ClientWebSocket? socket = null;
        string? connectedUrl = null;

        foreach (var baseUrl in _connectPolicy.HostsToTry(candidates))
        {
            ct.ThrowIfCancellationRequested();
            var safeHost = SafeHost(baseUrl);
            _lastStatus = _authBlockStatus ?? _connectPolicy.CurrentCloseStatus ?? $"Connecting ({safeHost})…";
            _log.Information("WindUpKey connect attempt host={Host}", safeHost);

            try
            {
                socket = await OpenWebSocketAsync(baseUrl, generation, ct).ConfigureAwait(false);
                connectedUrl = baseUrl;
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _log.Warning(ex, "WindUpKey connect failed host={Host}", safeHost);
            }
        }

        if (socket is null || connectedUrl is null)
        {
            throw new NoRelayHostReachableException(lastError);
        }

        var connectedHost = SafeHost(connectedUrl);
        _log.Information("WindUpKey WebSocket open host={Host} state={State}", connectedHost, socket.State);

        // The character changed while the socket opened: don't register the old one; start over with the new one.
        if (_connectPolicy.Epoch != epoch)
            throw new StaleConnectAttemptException();
#if WINDUP_TESTING
        // 04-D7: the self-test's outage began while this socket opened: don't register; the loop waits out the hold.
        if (_selfTestOutageHold)
            throw new StaleConnectAttemptException();
#endif

        // The sticky host is saved only once this host accepts the register (OnRegisterAccepted).
        _connectionUrl = connectedUrl;
        Volatile.Write(ref _connectionEpoch, epoch);
        _registeredSecret = relaySecret;
#if WINDUP_TESTING
        _selfTestRegisteredKey = pairingKey;
#endif

        // 05-D6: no token in the body; the upgrade's Authorization header already carried it.
        _registerRejection = null;
        await SendEnvelopeAsync(RelayWireRules.Register(pairingKey, relaySecret), ct).ConfigureAwait(false);
        _log.Information("WindUpKey registered on relay as key={Key}", LogRedact.Key(pairingKey));

        // The sends below use what was read with the key and secret, and stop if the character changed (or a login,
        // import or revert reset the cycle) meanwhile: the loop then reconnects for the current one.
        // Unpairs made while offline (or whose send failed) go first, so the relay drops those pairs before partners
        // are re-asserted. They leave the queue when the register is accepted (pairSync); until then, a resend is
        // harmless.
        var sentRemovals = new List<PendingPairRemoval>();
        foreach (var removal in removals)
        {
            ThrowIfStale(epoch);
            var revoked = !removal.RevokeOwner
                          || await SendEnvelopeAsync(Envelope.Create(MessageTypes.OwnerRevoked, new OwnerRevokedPayload
                          {
                              From = pairingKey,
                              To = removal.PeerKey,
                          }), ct).ConfigureAwait(false);
            if (!revoked)
                break;

            var removed = await SendEnvelopeAsync(Envelope.Create(MessageTypes.PairRemove, new PairRemovePayload
            {
                PeerKey = removal.PeerKey,
            }), ct).ConfigureAwait(false);
            if (!removed)
                break;

            sentRemovals.Add(removal);
        }

        Volatile.Write(ref _removalsAwaitingSync, new SentPairRemovals(epoch, pairingKey, sentRemovals));

        // Re-submit pending pair keys after reconnect.
        foreach (var key in pendingKeys)
        {
            ThrowIfStale(epoch);
            await SendEnvelopeAsync(Envelope.Create(MessageTypes.PairSubmit, new PairSubmitPayload
            {
                RequestId = Guid.NewGuid().ToString("N"),
                MyKey = pairingKey,
                TheirKey = key,
            }), ct).ConfigureAwait(false);
        }

        // Re-assert established partners so the relay can rebuild durable pairs after upgrades/restarts.
        foreach (var partner in partners)
        {
            var peer = PairingKeyUtil.Normalize(partner.Key);
            if (!PairingKeyUtil.IsValid(peer))
                continue;
            if (string.Equals(peer, pairingKey, StringComparison.Ordinal))
                continue;
            if (pendingKeys.Contains(peer, StringComparer.Ordinal))
                continue;

            ThrowIfStale(epoch);
            await SendEnvelopeAsync(Envelope.Create(MessageTypes.PairSubmit, new PairSubmitPayload
            {
                RequestId = Guid.NewGuid().ToString("N"),
                MyKey = pairingKey,
                TheirKey = peer,
            }), ct).ConfigureAwait(false);
        }

        // Re-assert ownership once on connect (covers grants made while disconnected).
        // Relay outbox already delivers live grants to offline owners; do not resend on presence polls.
        if (isDoll)
        {
            foreach (var partner in partners)
            {
                if (!partner.IsOwner)
                    continue;
                var peer = PairingKeyUtil.Normalize(partner.Key);
                if (!PairingKeyUtil.IsValid(peer))
                    continue;
                if (string.Equals(peer, pairingKey, StringComparison.Ordinal))
                    continue;

                ThrowIfStale(epoch);
                await SendEnvelopeAsync(Envelope.Create(MessageTypes.OwnerGrant, new OwnerGrantPayload
                {
                    From = pairingKey,
                    To = peer,
                    Identity = ownerIdentity,
                }), ct).ConfigureAwait(false);
            }
        }

        return new RelayConnection(socket, connectedUrl, candidates, profile);
    }

    /// <summary>The connect cycle was reset since <paramref name="epoch"/> (character switch, login, import, revert).</summary>
    private void ThrowIfStale(int epoch)
    {
        if (_connectPolicy.Epoch != epoch)
            throw new StaleConnectAttemptException();
    }

    /// <summary>
    /// Returns the active character's relay secret as it is on disk now (<see cref="RelayIdentityStore"/>),
    /// generating and saving one the first time it is needed. Null when the identity file is unavailable, or
    /// read-only without this character's entry: the client then doesn't register at all.
    /// Framework thread only. Never log the result.
    /// </summary>
    private string? EnsureRelaySecret()
    {
        var secret = _identityStore.EnsureSecret(_config.ActiveContentId, out var generated);
        if (generated)
            _log.Information("WindUpKey generated a relay identity for this character");
        return secret;
    }

    /// <summary>
    /// True while a character is logged in and is the active profile, so the relay identity UI acts on the
    /// right character (not a stale one on the title screen). Stays true through zone loads and teleports.
    /// Framework thread (UI).
    /// </summary>
    public bool CanManageRelayIdentity => _characterReady && !string.IsNullOrEmpty(_config.ActiveContentId);

    /// <summary>
    /// The ContentId whose relay identity the UI manages, or empty when <see cref="CanManageRelayIdentity"/> is false.
    /// Lets the import confirmation check the character didn't change (or log out) while it was open; a zone load
    /// doesn't change it. Framework thread (UI).
    /// </summary>
    public string RelayIdentityContentId => CanManageRelayIdentity ? _config.ActiveContentId : string.Empty;

    /// <summary>Whether the relay identity file can be used right now (as of its last read or write). Framework thread (UI).</summary>
    public RelayIdentityStoreStatus RelayIdentityStatus => _identityStore.Status;

    /// <summary>The active character's relay secret for the Copy button, or empty. Framework thread (UI). Never log it.</summary>
    public string ActiveRelaySecret =>
        CanManageRelayIdentity ? _identityStore.GetSecret(_config.ActiveContentId) : string.Empty;

    /// <summary>
    /// What <see cref="ImportRelaySecret"/> would do with this pasted value, without changing anything.
    /// Framework thread (UI). Lets the UI confirm before replacing the current identity.
    /// </summary>
    public RelayIdentityImport CheckRelaySecretImport(string raw) =>
        CanManageRelayIdentity
            ? _identityStore.CheckImport(_config.ActiveContentId, raw)
            : RelayIdentityImport.NoCharacter;

    /// <summary>
    /// Replaces the active character's relay secret with one copied from another install, keeping the old one
    /// as its previous secret, then reconnects immediately (which also clears an auth block). Importing the
    /// saved secret changes nothing on disk; it reconnects (<see cref="RelayIdentityImport.Reconnected"/>) when
    /// blocked or when this session registered with a different value. Refused when <paramref name="contentId"/> (the character the user
    /// confirmed for) is no longer the active one. Framework thread (UI).
    /// </summary>
    public RelayIdentityImport ImportRelaySecret(string raw, string contentId)
    {
        if (!CanManageRelayIdentity
            || !string.Equals(_config.ActiveContentId, contentId, StringComparison.Ordinal))
            return RelayIdentityImport.NoCharacter;

        var result = _identityStore.Import(_config.ActiveContentId, raw);
        if (result == RelayIdentityImport.Unchanged)
        {
            // Already saved (for example by another game client), but the relay rejected this session, or this
            // session registered with a different value: reconnect with the saved one.
            var sent = _registeredSecret;
            var differs = sent is not null
                          && !string.Equals(sent, _identityStore.GetSecret(_config.ActiveContentId), StringComparison.Ordinal);
            if (_authBlockStatus is null && !differs)
                return result;

            _log.Information("WindUpKey relay identity already saved; reconnecting with it");
            Start();
            return RelayIdentityImport.Reconnected;
        }

        if (result != RelayIdentityImport.Replace)
            return result;

        _log.Information("WindUpKey relay identity imported");
        Start();
        return result;
    }

    /// <summary>True when the active character has a previous relay identity to go back to.</summary>
    public bool CanRevertRelaySecret =>
        CanManageRelayIdentity && _identityStore.CanRevert(_config.ActiveContentId);

    /// <summary>
    /// Swaps the active character's current and previous relay secrets, then reconnects immediately.
    /// Framework thread (UI). Returns false when there is no previous identity or it couldn't be saved.
    /// </summary>
    public bool RevertRelaySecret()
    {
        if (!CanManageRelayIdentity || !_identityStore.Revert(_config.ActiveContentId))
            return false;

        _log.Information("WindUpKey relay identity reverted to the previous one");
        Start();
        return true;
    }

    /// <summary>The status shown while not connecting for a reason that isn't a host problem.</summary>
    private string ConnectWaitStatus(RelayConnectStep step) => step switch
    {
        RelayConnectStep.WaitIdentityUnavailable =>
            "Relay identity file busy (another game client may be using it) or unreadable (see dalamud.log). Retrying…",
        RelayConnectStep.WaitIdentityNotWritable =>
            "The relay identity file or its .lock file in pluginConfigs is read-only or not writable, so no relay " +
            "identity can be saved for this character (see dalamud.log). Make them writable; retrying…",
        RelayConnectStep.WaitIdentityReadOnly =>
            "The relay identity file is from a newer version of Wind-Up Key and has no identity for this character. " +
            "Update the plugin to connect.",
        RelayConnectStep.WaitForUser => _authBlockStatus ?? "Waiting for Reconnect…",
        _ => "Waiting for character data…",
    };

    /// <summary>
    /// Framework thread. Shows the block the policy just entered (or changed): stops reconnecting when it is full,
    /// and tells the player what to do — in chat once per occurrence, at most once per 30 minutes per character and
    /// block kind, plus one line when a partial block becomes full.
    /// </summary>
    private RelayHostVerdict ApplyHostVerdict(RelayHostVerdict verdict)
    {
        if (verdict == RelayHostVerdict.Blocked && _connectPolicy.BlockCode is { } code)
            ShowAuthBlock(code, _connectPolicy.Block);
        return verdict;
    }

    /// <summary>Framework thread. The relay replaced this connection's session (session_replaced): block until the user acts.</summary>
    private void ApplySessionReplaced()
    {
#if WINDUP_TESTING
        Interlocked.Increment(ref _sessionReplacedSeen);
#endif
        if (_connectPolicy.SessionReplaced(Volatile.Read(ref _connectionEpoch)))
            ShowAuthBlock(ErrorCodes.SessionReplaced, RelayAuthBlock.Full);
    }

    private void ShowAuthBlock(string code, RelayAuthBlock block)
    {
        // A kept-aside copy of a damaged identity file may hold the secret the relay expects.
        var backup = code == ErrorCodes.SessionReplaced ? null : _identityStore.NewestBackupFileName();
        var status = RelayConnectPolicy.BlockStatus(code, block, backup);

        var first = _authBlockStatus is null;
        _authBlockStatus = status;
        _lastStatus = status;
        if (first || block == RelayAuthBlock.Full)
        {
            _log.Warning(
                "WindUpKey relay rejected this session ({Code}); {What}",
                code,
                block == RelayAuthBlock.Full ? "not reconnecting" : "still trying unreachable hosts");
        }

        // Chat at most once per 30 minutes per character and block kind (decisions 31 and 33), plus one line when a
        // partial block becomes full (decision 37); the status line above always updates.
        if (_connectPolicy.ClaimBlockChat(_config.ActiveContentId, first, DateTime.UtcNow))
            PluginChat.PrintError(_chat, status);

        if (block == RelayAuthBlock.Full)
            CloseSocket();
    }

    /// <summary>
    /// Framework thread. The relay accepted this connection's register (it sent pairSync): save the host as the
    /// sticky one, forget the rejections, and lift a partial block.
    /// </summary>
    private void OnRegisterAccepted()
    {
        _registerAccepted = true;
#if WINDUP_TESTING
        Interlocked.Increment(ref _acceptedConnections);
#endif
        _backoff.RegisterAccepted();
        _connectPolicy.ClearCloseStatus();
        if (_connectionUrl is { } url)
        {
            _config.RelayUrl = url;
            if (!string.Equals(_config.LastSuccessfulRelayUrl, url, StringComparison.Ordinal))
            {
                _config.LastSuccessfulRelayUrl = url;
                _config.Save();
            }
        }

        if (!_connectPolicy.Accepted(Volatile.Read(ref _connectionEpoch)))
            return;

        _authBlockStatus = null;
        _log.Information("WindUpKey relay host={Host} accepted the relay identity; auth block lifted", SafeHost(_connectionUrl ?? string.Empty));
    }

    /// <summary>Lifts the auth block and forgets which hosts rejected the identity (login, switch, import, revert).</summary>
    private void ClearAuthBlock()
    {
        // Also starts a new epoch without the last close's status (05-D13). Start() comes here too, so a new loop
        // generation is always a new epoch.
        _connectPolicy.Reset();
        _authBlockStatus = null;
    }

    private async Task<ClientWebSocket> OpenWebSocketAsync(string baseUrl, int generation, CancellationToken ct)
    {
        var uri = BuildUri(baseUrl);
        var socket = new ClientWebSocket();
        // Shared token goes in a header, not the URL (the relay accepts both). Same setup as the self-test's probes.
        RelaySocketSetup.Configure(socket.Options, _config.RelayToken);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Keep each attempt bounded, so an unreachable relay fails within this time and the loop goes on to its backoff.
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await socket.ConnectAsync(uri, timeoutCts.Token).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        if (Volatile.Read(ref _loopGeneration) != generation || ct.IsCancellationRequested)
        {
            socket.Dispose();
            ct.ThrowIfCancellationRequested();
            throw new OperationCanceledException("Relay loop was superseded.", ct);
        }

        ClientWebSocket? replaced;
        lock (_socketGate)
        {
            if (Volatile.Read(ref _loopGeneration) != generation || ct.IsCancellationRequested)
            {
                socket.Dispose();
                ct.ThrowIfCancellationRequested();
                throw new OperationCanceledException("Relay loop was superseded.", ct);
            }

            replaced = _socket;
            _socket = socket;
            _socketGeneration = generation;
        }
        replaced?.Dispose();
        return socket;
    }

    private static string SafeHost(string baseUrl)
    {
        try
        {
            var uri = new Uri(baseUrl);
            return $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";
        }
        catch
        {
            return "(invalid-url)";
        }
    }

    private static Uri BuildUri(string baseUrl) => new(baseUrl.Trim());

    /// <summary>
    /// Runs one connection until it ends. Returns auth_failed/auth_required when this host rejected the
    /// register (the caller then tries the next host), otherwise null.
    /// </summary>
    private async Task<string?> ReceiveLoopAsync(ClientWebSocket socket, int epoch, string profile, CancellationToken ct)
    {
        Volatile.Write(ref _staleInboundLogged, 0);
        // 03-D10: messages over the client's limit are discarded and the connection kept, logged once per connection.
        var pump = new RelayInboundPump(limit => _log.Warning(
            "WindUpKey discarded a relay message over {Limit} bytes; keeping the connection (logged once per connection)",
            limit));
        // 08-P2 (T5): a connection that ends without a close (a rejected host returning early, a dropped socket, a
        // cancel) still clears the last close's status for this epoch (05-D13); a close already recorded its own, which
        // the close branch's `break` must not clear.
        var closeSeen = false;
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var inbound = await pump.NextAsync(socket, ct).ConfigureAwait(false);
                if (inbound.Envelope is not { } envelope)
                {
#if WINDUP_TESTING
                    RecordSelfTestClose(inbound.CloseStatus, inbound.CloseDescription);
#endif
                    // 05-D9: a close that explains itself (the first-bind limit) is shown while reconnecting; any other
                    // close clears it. 05-D13: only for this connection's own epoch, so a close read after a character
                    // switch (or login, Reconnect, import, revert) can't carry its message into the new cycle.
                    _connectPolicy.RecordClose(epoch, inbound.CloseStatus, inbound.CloseDescription);
                    closeSeen = true;
                    // Relay refusals close with PolicyViolation; a secretless refusal sends no error first.
                    switch (RelayConnectPolicy.ClassifyClose(inbound.CloseStatus, inbound.CloseDescription))
                    {
                        case RelaySessionSignal.Rejected:
                            return inbound.CloseDescription;
                        case RelaySessionSignal.SessionReplaced:
                            await _framework.RunOnTick(ApplySessionReplaced, cancellationToken: ct)
                                .ConfigureAwait(false);
                            break;
                    }

                    break;
                }

#if WINDUP_TESTING
                // 04-D6: the self-test's replies never reach normal handling, so no chat line can appear; a partner's
                // presence answer to a self-test query is only observed, and still handled below.
                if (SelfTestTap.Inspect(_selfTestTap, envelope) == TapVerdict.Consume)
                    continue;
#endif

                // Socket IO and parsing the envelope stay on this worker (03-D10); all plugin/config/game state is
                // applied on Dalamud's framework thread so lock hooks cannot change at an unsafe time.
                // 06-A9: a message is for the connection (epoch) and character it arrived on. A switch closes this
                // socket, but a message already queued here would still run in the next tick, so it is checked again
                // when handled and dropped once the active character or the connect cycle has changed: it never applies
                // to another one.
                await _framework.RunOnTick(
                        () => RelayConnectPolicy.InboundStillCurrent(epoch, profile, _connectPolicy.Epoch, _config.ActiveContentId)
                            ? HandleMessageAsync(envelope, ct)
                            : DropStaleInbound(),
                        cancellationToken: ct)
                    .ConfigureAwait(false);

                if (_registerRejection is { } rejected)
                    return rejected;
            }
        }
        finally
        {
            if (!closeSeen)
                _connectPolicy.RecordClose(epoch, null, null);
        }

        return _registerRejection;
    }

    /// <summary>
    /// 06-A9: framework thread. A message from a superseded connection is dropped unhandled, logged once per connection
    /// with nothing from the message itself.
    /// </summary>
    private Task DropStaleInbound()
    {
        if (Interlocked.Exchange(ref _staleInboundLogged, 1) == 0)
            _log.Debug("WindUpKey dropped a relay message from a superseded connection (character switch or reconnect)");

        return Task.CompletedTask;
    }

    private async Task HandleMessageAsync(Envelope envelope, CancellationToken ct)
    {
        switch (envelope.Type)
        {
            case MessageTypes.Wind:
                await HandleInboundWindAsync(envelope, ct).ConfigureAwait(false);
                break;
            case MessageTypes.WindRequest:
                HandleInboundWindRequest(envelope);
                break;
            case MessageTypes.Unwind:
                await HandleInboundUnwindAsync(envelope, ct).ConfigureAwait(false);
                break;
            case MessageTypes.WindResult:
                HandleWindResult(envelope);
                break;
            case MessageTypes.Error:
                await HandleErrorAsync(envelope, ct).ConfigureAwait(false);
                break;
            case MessageTypes.PairEstablished:
                HandlePairEstablished(envelope);
                break;
            case MessageTypes.PairSync:
                // Relay-originated only (never routed), and sent once the register is accepted.
                OnRegisterAccepted();
                CompleteSentPairRemovals();
                HandlePairSync(envelope);
                break;
            case MessageTypes.PairRemove:
                HandlePairRemove(envelope);
                break;
            case MessageTypes.DeliveryQueued:
                HandleDeliveryQueued(envelope);
                break;
            case MessageTypes.DeliveryDelivered:
                HandleDeliveryDelivered(envelope);
                break;
            case MessageTypes.PresenceQuery:
                await HandleInboundPresenceQueryAsync(envelope, ct).ConfigureAwait(false);
                break;
            case MessageTypes.PresenceResult:
                HandlePresenceResult(envelope);
                break;
            case MessageTypes.OwnerGrant:
                HandleOwnerGrant(envelope);
                break;
            case MessageTypes.OwnerRevoked:
                HandleOwnerRevoked(envelope);
                break;
            case MessageTypes.OwnerSettingsQuery:
                await HandleInboundOwnerSettingsQueryAsync(envelope, ct).ConfigureAwait(false);
                break;
            case MessageTypes.OwnerSettingsResult:
                HandleOwnerSettingsResult(envelope);
                break;
            case MessageTypes.OwnerSettingsUpdate:
                await HandleInboundOwnerSettingsUpdateAsync(envelope, ct).ConfigureAwait(false);
                break;
            case MessageTypes.OwnerSettingsAck:
                HandleOwnerSettingsAck(envelope);
                break;
            case MessageTypes.Call:
                await HandleInboundCallAsync(envelope, ct).ConfigureAwait(false);
                break;
            case MessageTypes.CallAck:
                HandleCallAck(envelope);
                break;
            case MessageTypes.CallResult:
                HandleCallResult(envelope);
                break;
            case MessageTypes.Ping:
                await SendEnvelopeAsync(Envelope.Create(MessageTypes.Pong, new { }), ct).ConfigureAwait(false);
                break;
            case MessageTypes.Pong:
                break;
            default:
                break;
        }
    }

    private async Task HandleInboundWindAsync(Envelope envelope, CancellationToken ct)
    {
        var payload = envelope.GetPayload<WindPayload>();
        if (payload is null)
            return;

        var fromKey = PairingKeyUtil.Normalize(payload.From);
        var fromSelf = string.Equals(fromKey, _config.PairingKey, StringComparison.Ordinal);

        // Stealth: unpaired senders get no reply (do not prove we are online).
        if (!(_config.IsDebugEnabled && fromSelf) && !_consent.IsPairedByKey(fromKey))
            return;

        // Self-wind (debug) still requires Can wind me on the self-key pair.
        var allowed = ((_config.IsDebugEnabled && fromSelf) || _config.IsDoll)
                      && _consent.CanReceiveWindFromKey(fromKey);

        if (!allowed)
        {
            await SendEnvelopeAsync(Envelope.Create(MessageTypes.Error, new ErrorPayload
            {
                RequestId = payload.RequestId,
                To = fromKey,
                Code = ErrorCodes.NotAllowed,
                Message = GenericWindFailure,
            }), ct).ConfigureAwait(false);
            return;
        }

        var pair = _config.FindPairByKey(fromKey);
        var winderIdentity = pair is not null
            ? pair.GetMessageLabel()
            : fromSelf
                ? _cachedIdentity
                : null;

        var remaining = _timer.AddHours(payload.Hours, winderIdentity);
        await SendEnvelopeAsync(Envelope.Create(MessageTypes.WindResult, new WindResultPayload
        {
            RequestId = payload.RequestId,
            From = _config.PairingKey,
            To = fromKey,
            RemainingSeconds = remaining.TotalSeconds,
            RemainingDisplay = WindTimerService.FormatRemaining(remaining),
        }), ct).ConfigureAwait(false);
    }

    private async Task HandleInboundUnwindAsync(Envelope envelope, CancellationToken ct)
    {
        var payload = envelope.GetPayload<UnwindPayload>();
        if (payload is null)
            return;

        var fromKey = PairingKeyUtil.Normalize(payload.From);
        var fromSelf = string.Equals(fromKey, _config.PairingKey, StringComparison.Ordinal);

        if (!(_config.IsDebugEnabled && fromSelf) && !_consent.IsPairedByKey(fromKey))
            return;

        var allowed = ((_config.IsDebugEnabled && fromSelf) || _config.IsDoll)
                      && _consent.CanReceiveUnwindFromKey(fromKey);

        if (!allowed)
        {
            await SendEnvelopeAsync(Envelope.Create(MessageTypes.Error, new ErrorPayload
            {
                RequestId = payload.RequestId,
                To = fromKey,
                Code = ErrorCodes.NotAllowed,
                Message = GenericWindFailure,
            }), ct).ConfigureAwait(false);
            return;
        }

        if (await RefuseIfSafewordPausedAsync(envelope.Type, payload.RequestId, fromKey, ct).ConfigureAwait(false))
            return;

        var pair = _config.FindPairByKey(fromKey);
        var winderIdentity = pair is not null
            ? pair.GetMessageLabel()
            : fromSelf
                ? _cachedIdentity
                : null;

        _timer.ClearWind(winderIdentity);
        var remaining = _timer.RemainingForWinder();
        await SendEnvelopeAsync(Envelope.Create(MessageTypes.WindResult, new WindResultPayload
        {
            RequestId = payload.RequestId,
            From = _config.PairingKey,
            To = fromKey,
            RemainingSeconds = remaining.TotalSeconds,
            RemainingDisplay = WindTimerService.FormatRemaining(remaining),
        }), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 06-B1: framework thread, when the message is handled (so a message queued while offline is checked on delivery).
    /// While the safeword pauses remote control, an unwind, call or owner settings update from a partner allowed to send
    /// it is refused with <see cref="ErrorCodes.SafewordPaused"/> and nothing is applied. Checked after the stealth and
    /// permission checks, so only a partner who could otherwise do it learns of the pause. Returns true when refused.
    /// </summary>
    private async Task<bool> RefuseIfSafewordPausedAsync(string messageType, string? requestId, string fromKey, CancellationToken ct)
    {
        if (!SafewordPause.Refuses(messageType) || !_timer.IsRemoteControlPaused)
            return false;

        await SendEnvelopeAsync(Envelope.Create(MessageTypes.Error, new ErrorPayload
        {
            RequestId = requestId,
            To = fromKey,
            Code = ErrorCodes.SafewordPaused,
            Message = SafewordPausedFailure,
        }), ct).ConfigureAwait(false);
        return true;
    }

    private void HandleInboundWindRequest(Envelope envelope)
    {
        var payload = envelope.GetPayload<WindRequestPayload>();
        if (payload is null)
            return;

        var fromKey = PairingKeyUtil.Normalize(payload.From);
        if (!_consent.IsPairedByKey(fromKey))
            return;

        var percent = Math.Clamp(payload.PercentRemaining, 0, 100);
        var label = GetPairMessageLabel(fromKey);
        PluginChat.Print(
            _chat,
            $"A needy doll requests your attention. {label} is at {percent}% winding remaining and is requesting additional winding.",
            PluginChat.Blue);
    }

    private void HandleWindResult(Envelope envelope)
    {
        var payload = envelope.GetPayload<WindResultPayload>();
        if (payload is null)
            return;

        var fromKey = PairingKeyUtil.Normalize(payload.From);
        var aboutSelf = string.Equals(fromKey, _config.PairingKey, StringComparison.Ordinal);
        var hadPendingWind = TryTakePendingWind(payload.RequestId, out var windHours);

        // Dolls must not learn their own remaining time. Debug self-wind is the exception so
        // the UI/testing path can still show winder-style feedback.
        if (_config.IsDoll && aboutSelf && !_config.IsDebugEnabled)
            return;

        // Winder hears wind-up; doll about-self already played via OnWoundReceived.
        if (hadPendingWind && !(_config.IsDoll && aboutSelf))
            _sounds.PlayWind(TimeSpan.FromHours(windHours));

        var remaining = TimeSpan.FromSeconds(Math.Max(0, payload.RemainingSeconds));
        var label = GetPairMessageLabel(fromKey);
        _notifier.NotifyWinderRemaining(label, remaining);
    }

    private async Task HandleErrorAsync(Envelope envelope, CancellationToken ct)
    {
        var payload = envelope.GetPayload<ErrorPayload>();
        if (payload is null)
            return;

        // Relay-originated session errors never set `to`; routed (peer-sent) errors always do.
        switch (RelayConnectPolicy.ClassifyError(payload.Code, payload.To))
        {
            case RelaySessionSignal.Ignore:
                return;
            case RelaySessionSignal.SessionReplaced:
                ApplySessionReplaced();
                return;
            case RelaySessionSignal.Rejected:
                // Per host: the receive loop ends this connection and the connect loop tries the next host.
                _registerRejection = payload.Code;
                return;
        }

        // 03-D6: the relay didn't queue it for the offline partner; a fixed line, never the error's own message.
        var queueFull = payload.Code == ErrorCodes.QueueFull;
        if (!string.IsNullOrEmpty(payload.RequestId))
        {
            // Calls are deliberately silent on the owner side, including relay rejection/errors.
            if (TryTakePendingCall(payload.RequestId))
                return;
            lock (_pendingWindLock)
            {
                if (_pendingWindRequestIds.Remove(payload.RequestId))
                {
                    var message = queueFull
                        ? QueueFullFailure
                        : payload.Code == ErrorCodes.WindRequestCooldown
                            ? "You may request winding only once per hour."
                            : "Unable to send that winding request.";
                    _notifier.NotifyWinderError(message);
                    return;
                }
            }
            TryTakePendingWind(payload.RequestId, out _);
            if (TryTakePendingOwnerRequest(payload.RequestId, out var dollKey))
            {
                var message = queueFull
                    ? QueueFullFailure
                    : payload.Code == ErrorCodes.TargetOffline
                        ? "That doll is offline."
                        : payload.Code == ErrorCodes.NotAllowed
                            ? "Not allowed to manage that doll."
                            : payload.Code == ErrorCodes.SafewordPaused
                                ? SafewordPausedFailure
                                : GenericOwnerSettingsFailure;
                // 06-B3: after a pause refused a change, the window shows what's actually set, not the refused values.
                // 08-T6 (D10): the last confirmed values may be stale (another owner may have changed them since), so
                // the doll is asked again; she still answers queries while paused (06-B). Its result keeps the message.
                // At most one re-query waits at a time, so a refusal of the re-query itself (only a modified client
                // would send one) can't start a query-refusal loop.
                if (payload.Code == ErrorCodes.SafewordPaused)
                {
                    var requery = !OwnerSettingsReQueryPending(dollKey);
                    RestoreOwnerSettingsAfterRefusal(dollKey, message, keepErrorOnNextResult: true);
                    if (requery)
                        Forget(ReQueryOwnerSettingsAsync(dollKey), "owner settings re-query", background: true);
                }
                else
                    SetOwnerSettingsError(dollKey, message);
                _notifier.NotifyWinderError(message);
                await Task.CompletedTask.ConfigureAwait(false);
                return;
            }
        }

        if (queueFull)
        {
            _notifier.NotifyWinderError(QueueFullFailure);
            return;
        }

        // 06-B1: the doll refused an unwind (not tracked by requestId) because her safeword paused remote control.
        if (payload.Code == ErrorCodes.SafewordPaused)
        {
            _notifier.NotifyWinderError(SafewordPausedFailure);
            return;
        }

        if (payload.Code == ErrorCodes.PairKeyCollision)
        {
            // Key is stable per character profile; another session (or stale socket) holds it.
            _log.Warning("WindUpKey pairing key in use — reconnecting without changing key");
            CloseSocket();
            return;
        }

        if (payload.Code == ErrorCodes.RateLimited)
        {
            // 07-A2: a burst of refused messages shows this once per 30 s, not once each.
            if (_relayBusyNotice.TryShow())
                _notifier.NotifyWinderError("Relay is busy; try again shortly.");
            return;
        }

        // Offline fire-and-forget (no requestId) — nothing to surface to the winder.
        if (payload.Code == ErrorCodes.TargetOffline && string.IsNullOrEmpty(payload.RequestId))
            return;

        if (payload.Code is ErrorCodes.PairFailed)
        {
            _notifier.NotifyWinderError(GenericPairFailure);
            return;
        }

        _notifier.NotifyWinderError(GenericWindFailure);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void RememberPendingWind(string requestId, double hours)
    {
        lock (_pendingWindLock)
            _pendingWindHoursByRequestId[requestId] = hours;
    }

    /// <summary>
    /// Fills empty local-only Name@World on a pair after a successful context-menu resolve.
    /// </summary>
    private void RememberPartnerIdentity(PairedPartner pair, string targetIdentity)
    {
        if (pair.IsIdentitySaved || !string.IsNullOrWhiteSpace(pair.Identity))
            return;

        var normalized = PlayerIdentity.Normalize(targetIdentity);
        if (string.IsNullOrEmpty(normalized))
            return;

        pair.Identity = normalized;
        _config.Save();
    }

    private string GetPairMessageLabel(string pairingKey)
    {
        var key = PairingKeyUtil.Normalize(pairingKey);
        var pair = _config.FindPairByKey(key);
        var label = pair?.GetMessageLabel();
        return string.IsNullOrWhiteSpace(label) ? key : label;
    }

    private bool TryTakePendingWind(string? requestId, out double hours)
    {
        hours = 0;
        if (string.IsNullOrEmpty(requestId))
            return false;

        lock (_pendingWindLock)
        {
            if (!_pendingWindHoursByRequestId.Remove(requestId, out hours))
                return false;
            return true;
        }
    }

    private void HandlePairEstablished(Envelope envelope)
    {
        var payload = envelope.GetPayload<PairEstablishedPayload>();
        if (payload is null)
            return;

        var peerKey = PairingKeyUtil.Normalize(payload.PeerKey);
        if (IsUnpairedHere(peerKey))
            return;

        var announce = RelaySyncRules.AnnouncesSyncChanges(_config.HasCompletedInitialSetup);
        var newHere = IsNewToThisPc(peerKey);
        var added = EnsurePeerPair(peerKey);
        var pair = _config.FindPairByKey(peerKey);
        if (added && announce && newHere && pair is not null)
            PrintSyncChange(PairSyncChange.PartnerAdded, pair);

        // 02-D29: the relay just made or restored this pair, so its consent row is missing or stale.
        if (RelaySyncRules.PushesAfterPairing(_config.IsDoll, pair is not null))
            QueueDollStatePush();
    }

    /// <summary>
    /// The register was accepted (pairSync), so the relay has the unpairs this connection sent before it: they leave
    /// the queue, and this connection's sync can't bring those partners back. Framework thread.
    /// </summary>
    private void CompleteSentPairRemovals()
    {
        var sent = Interlocked.Exchange(ref _removalsAwaitingSync, null);
        if (sent is null
            || sent.Removals.Count == 0
            || sent.Epoch != _connectPolicy.Epoch
            || !string.Equals(sent.PairingKey, _config.PairingKey, StringComparison.Ordinal))
            return;

        foreach (var removal in sent.Removals)
            _unpairedThisConnection.Add(removal.PeerKey);

        if (PairRemovalQueue.Complete(_config.PendingPairRemovals, sent.Removals))
        {
            _config.Save();
            _log.Information("WindUpKey sent {Count} unpair(s) made while offline", sent.Removals.Count);
        }
    }

    /// <summary>
    /// This character unpaired the key and the relay may not have it yet (queued, or sent on this connection), so
    /// relay sync must not bring the partner back. Framework thread.
    /// </summary>
    private bool IsUnpairedHere(string peerKey) =>
        _unpairedThisConnection.Contains(peerKey) || PairRemovalQueue.Contains(_config.PendingPairRemovals, peerKey);

    /// <summary>Neither a partner nor a pending request made on this PC (a pairing done on another PC).</summary>
    private bool IsNewToThisPc(string peerKey) =>
        _config.FindPairByKey(peerKey) is null
        && !_config.PendingPartnerKeys.Contains(peerKey, StringComparer.Ordinal);

    private void PrintSyncChange(PairSyncChange change, PairedPartner pair) =>
        PluginChat.Print(_chat, RelaySyncRules.Describe(change, pair.GetChosenName()), PluginChat.Yellow);

    private void HandlePairSync(Envelope envelope)
    {
        var payload = envelope.GetPayload<PairSyncPayload>();
        if (payload is null)
            return;
#if WINDUP_TESTING
        _lastPeerKeys = (payload.PeerKeys ?? []).Select(PairingKeyUtil.Normalize).ToList();
#endif

        // Remote stamps dated well past this PC's clock would win last-write-wins until then: treated as absent.
        var now = DateTimeOffset.UtcNow;
        var consentByPeer = new Dictionary<string, (PairConsentSyncEntry Entry, DateTimeOffset Updated)>(StringComparer.Ordinal);
        if (payload.Peers is not null)
        {
            foreach (var entry in payload.Peers)
            {
                var key = PairingKeyUtil.Normalize(entry.PeerKey);
                if (PairingKeyUtil.IsValid(key) && RelaySyncRules.TryReadRemoteStamp(entry.UpdatedUtc, now, out var updated))
                    consentByPeer[key] = (entry, updated);
            }
        }

        // Partners come only from peerKeys (the relay's established pairs). A consent row updates a partner this PC
        // already knows, or one added from peerKeys in this sync, and never adds one.
        var targets = RelaySyncRules.PairSyncTargets(payload.PeerKeys, consentByPeer.Keys, _config.PairingKey);

        var changed = false;
        var needPush = false;

        // Partners added or consent turned on by another PC are told in chat, except before the profile has completed its initial setup.
        var announce = RelaySyncRules.AnnouncesSyncChanges(_config.HasCompletedInitialSetup);
        var announcements = new List<(PairSyncChange Change, PairedPartner Pair)>();

        foreach (var (peerKey, mayAdd) in targets)
        {
            if (IsUnpairedHere(peerKey))
                continue;

            var newHere = IsNewToThisPc(peerKey);
            if (mayAdd && EnsurePeerPair(peerKey, save: false))
            {
                changed = true;

                // 02-D29: a pair made or restored here: the relay's consent row and the doll's other PCs catch up.
                needPush |= RelaySyncRules.PushesAfterPairing(_config.IsDoll, pairedHere: true);
            }

            var pair = _config.FindPairByKey(peerKey);
            if (pair is null)
                continue;

            var wasOwner = pair.IsOwner;
            var couldCall = pair.CanCallMe;
            if (consentByPeer.TryGetValue(peerKey, out var row))
            {
                var (remote, remoteUpdated) = row;
                if (pair.ConsentUpdatedUtc is not { } localUpdated || remoteUpdated > localUpdated)
                {
                    pair.CanWindMe = remote.CanWindMe;
                    pair.CanUnwindMe = remote.CanUnwindMe;
                    pair.IsOwner = remote.IsOwner;
                    pair.CanCallMe = remote.CanCallMe;
                    pair.ConsentUpdatedUtc = remoteUpdated;
                    changed = true;
                }
                else if (localUpdated > remoteUpdated)
                {
                    needPush = true;
                }
            }
            else if (_config.IsDoll && (pair.ConsentUpdatedUtc is not null
                     || pair.CanWindMe || pair.CanUnwindMe || pair.IsOwner || pair.CanCallMe))
            {
                needPush = true;
            }

            if (announce)
            {
                foreach (var change in RelaySyncRules.Changes(newHere, wasOwner, couldCall, pair.IsOwner, pair.CanCallMe))
                    announcements.Add((change, pair));
            }
        }

        if (_config.IsDoll)
        {
            if (RelaySyncRules.TryReadRemoteStamp(payload.WindUpdatedUtc, now, out var remoteWindUpdated))
            {
                if (_config.WindUpdatedUtc is not { } localWindUpdated || remoteWindUpdated > localWindUpdated)
                {
                    DateTimeOffset? expiry = null;
                    if (SyncStamp.TryParse(payload.ExpiryUtc, out var parsedExpiry))
                    {
                        expiry = parsedExpiry;
                    }

                    _timer.ApplySyncedExpiry(expiry, remoteWindUpdated);
                }
                else if (localWindUpdated > remoteWindUpdated)
                {
                    needPush = true;
                }
            }
            else if (_config.WindUpdatedUtc is not null || _config.ExpiryUtc is not null)
            {
                needPush = true;
            }
        }

        if (changed)
            _config.Save();

        foreach (var (change, pair) in announcements)
            PrintSyncChange(change, pair);

        if (needPush)
            QueueDollStatePush();
    }

    /// <summary>
    /// Restores a peer key entry. Consent flags come from relay sync or stay at local defaults.
    /// </summary>
    private bool EnsurePeerPair(string peerKey, bool save = true)
    {
        if (!PairingKeyUtil.IsValid(peerKey))
            return false;

        if (string.Equals(peerKey, PairingKeyUtil.Normalize(_config.PairingKey), StringComparison.Ordinal))
            return false;

        var existing = _config.FindPairByKey(peerKey);
        var created = existing is null;
        if (created)
        {
            _config.PairedPartners.Add(new PairedPartner
            {
                Identity = string.Empty,
                PartnerKey = peerKey,
                CanWindMe = false,
            });
            _log.Information("WindUpKey paired with key={Key}", LogRedact.Key(peerKey));
        }

        var pendingCleared = _config.PendingPartnerKeys.RemoveAll(k =>
            string.Equals(k, peerKey, StringComparison.Ordinal)) > 0;
        if (save && (created || pendingCleared))
            _config.Save();
        return created || pendingCleared;
    }

    /// <summary>Marks consent dirty and queues a relay push (dolls only).</summary>
    public void NotifyConsentChanged(PairedPartner partner)
    {
        partner.ConsentUpdatedUtc = DateTimeOffset.UtcNow;
        _config.Save();
        QueueDollStatePush();
    }

    /// <summary>Push local doll wind + consent to the relay when connected (coalesced).</summary>
    public void QueueDollStatePush()
    {
        if (!_config.IsDoll)
            return;

        if (Interlocked.Exchange(ref _dollStatePushQueued, 1) == 1)
            return;

        Forget(PushDollStateAsync(), "doll-state push", background: true);
    }

    private async Task PushDollStateAsync()
    {
        try
        {
            await Task.Yield();
            Interlocked.Exchange(ref _dollStatePushQueued, 0);

            if (!_config.IsDoll || !IsConnected)
                return;

            // Config and chat belong to the framework thread.
            var payload = await _framework.RunOnTick(() =>
                {
                    // 08-P2 (K3a): nothing after an unload.
                    if (_disposed)
                        return null;

                    var windStampReset = ResetFutureLocalSyncStamps();
                    EnsureLocalSyncStamps(stampWind: !windStampReset);
                    return BuildDollStatePushPayload();
                })
                .ConfigureAwait(false);
            if (payload is null)
                return;

            await SendEnvelopeAsync(
                Envelope.Create(MessageTypes.DollStatePush, payload),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _dollStatePushQueued, 0);
            _log.Debug(ex, "WindUpKey dollStatePush failed");
        }
    }

    /// <summary>
    /// 02-D22: resets local consent stamps dated more than <see cref="SyncStamp.MaxFutureSkew"/> after this PC's
    /// clock (flags off, stamp cleared) and clears such a wind stamp (keeping the expiry, so the relay's value wins if
    /// it has one), then saves and tells the player once per partner. Returns whether the wind stamp was cleared.
    /// The next sync restores the permissions from a valid relay copy, and no <c>ownerRevoked</c> is sent (02-D27).
    /// Framework thread. Logs counts only.
    /// </summary>
    private bool ResetFutureLocalSyncStamps()
    {
        var reset = RelaySyncRules.ResetFutureLocalStamps(_config.PairedPartners, _config.WindUpdatedUtc, DateTimeOffset.UtcNow);
        if (!reset.Any)
            return false;

        if (reset.WindStampCleared)
            _config.WindUpdatedUtc = null;
        _config.Save();
        _log.Warning(
            "WindUpKey reset {Partners} partner permission stamp(s) dated in the future (wind stamp cleared: {Wind})",
            reset.Partners.Count,
            reset.WindStampCleared);

        foreach (var partner in reset.Partners)
            PluginChat.Print(_chat, RelaySyncRules.DescribeStampReset(partner.GetChosenName()), PluginChat.Yellow);

        return reset.WindStampCleared;
    }

    /// <param name="stampWind">
    /// False right after a future wind stamp was cleared: that push leaves the wind out instead of re-stamping the
    /// local expiry as new and overwriting the relay's value with it.
    /// </param>
    private void EnsureLocalSyncStamps(bool stampWind = true)
    {
        var now = DateTimeOffset.UtcNow;
        var touched = false;

        if (stampWind && _config.ExpiryUtc is not null && _config.WindUpdatedUtc is null)
        {
            _config.WindUpdatedUtc = now;
            touched = true;
        }

        foreach (var partner in _config.PairedPartners)
        {
            if (partner.ConsentUpdatedUtc is not null)
                continue;
            if (!partner.CanWindMe && !partner.CanUnwindMe && !partner.IsOwner && !partner.CanCallMe)
                continue;

            partner.ConsentUpdatedUtc = now;
            touched = true;
        }

        if (touched)
            _config.Save();
    }

    private DollStatePushPayload? BuildDollStatePushPayload()
    {
        var peers = new List<PairConsentSyncEntry>();
        foreach (var partner in _config.PairedPartners)
        {
            var peerKey = PairingKeyUtil.Normalize(partner.PartnerKey);
            if (!PairingKeyUtil.IsValid(peerKey))
                continue;
            if (partner.ConsentUpdatedUtc is not { } updated)
                continue;

            peers.Add(new PairConsentSyncEntry
            {
                PeerKey = peerKey,
                CanWindMe = partner.CanWindMe,
                CanUnwindMe = partner.CanUnwindMe,
                IsOwner = partner.IsOwner,
                CanCallMe = partner.CanCallMe,
                UpdatedUtc = updated.ToString("O"),
            });
        }

        var hasWind = _config.WindUpdatedUtc is not null;
        if (peers.Count == 0 && !hasWind)
            return null;

        return new DollStatePushPayload
        {
            Peers = peers,
            ExpiryUtc = _config.ExpiryUtc?.ToString("O"),
            WindUpdatedUtc = _config.WindUpdatedUtc?.ToString("O"),
            ClearWind = hasWind && _config.ExpiryUtc is null,
        };
    }

    private void HandleDeliveryQueued(Envelope envelope)
    {
        var payload = envelope.GetPayload<DeliveryStatusPayload>();
        if (payload is null || string.IsNullOrEmpty(payload.RequestId))
            return;

        // Keep pending wind/owner requestIds until windResult / settings result / error.
        _log.Information(
            "WindUpKey delivery queued type={Type} requestId={RequestId} to={To}",
            payload.Type,
            payload.RequestId,
            LogRedact.Key(payload.To));
    }

    private void HandleDeliveryDelivered(Envelope envelope)
    {
        var payload = envelope.GetPayload<DeliveryStatusPayload>();
        if (payload is null || string.IsNullOrEmpty(payload.RequestId))
            return;

        _log.Debug(
            "WindUpKey delivery delivered type={Type} requestId={RequestId} to={To}",
            payload.Type,
            payload.RequestId,
            LogRedact.Key(payload.To));
    }

    private void HandlePairRemove(Envelope envelope)
    {
        var payload = envelope.GetPayload<PairRemovePayload>();
        if (payload is null)
            return;

        var peerKey = PairingKeyUtil.Normalize(payload.PeerKey);
        var wasOwner = _config.FindPairByKey(peerKey) is { IsOwner: true };
        SilentRemovePartner(peerKey);
        if (wasOwner && IsConnected)
            Forget(SendOwnerRevokedAsync(peerKey), "owner revoke", background: true);
    }

    private async Task HandleInboundPresenceQueryAsync(Envelope envelope, CancellationToken ct)
    {
        var payload = envelope.GetPayload<PresenceQueryPayload>();
        if (payload is null)
            return;

        var fromKey = PairingKeyUtil.Normalize(payload.From);
        var paired = _consent.IsPairedByKey(fromKey);

        // Unpaired senders get stillPaired:false and nothing else, so a half-unpair heals on their side.
        if (!RelaySyncRules.TryAnswerPresence(fromKey, _config.PairingKey, _config.IsDebugEnabled, paired, out var stillPaired))
            return;

        await SendEnvelopeAsync(Envelope.Create(MessageTypes.PresenceResult, new PresenceResultPayload
        {
            RequestId = payload.RequestId,
            From = _config.PairingKey,
            To = fromKey,
            Online = true,
            // 08-D15: unset until this connection's pairSync arrived; the local list may not be loaded from it yet.
            StillPaired = RelaySyncRules.PresenceStillPairedAnswer(stillPaired, _registerAccepted),
        }), ct).ConfigureAwait(false);
    }

    private void HandlePresenceResult(Envelope envelope)
    {
        var payload = envelope.GetPayload<PresenceResultPayload>();
        if (payload is null)
            return;

        var peerKey = PairingKeyUtil.Normalize(payload.From);
        if (!PairingKeyUtil.IsValid(peerKey))
            return;

        SetPresence(peerKey, payload.Online ? PartnerPresence.Online : PartnerPresence.Offline);

        // 08-D19: removed here only; the relay keeps the pair, so the next pairSync brings the partner back until their own
        // queued unpair lands. Never an unpair on the relay: released clients (0.1.2.0 to 0.2.0.1) never sync their pair
        // list, so on a second PC or after a reinstall they answer false to every query. stillPaired is only ever sent by
        // the partner's client (unset until its register is accepted, 08-D15); the relay's own answer, online or
        // offline, leaves it unset.
        if (payload.StillPaired == false)
            SilentRemovePartner(peerKey);
    }

    private void SilentRemovePartner(string peerKey)
    {
        var local = RelaySyncRules.RemovePartnerLocally(_config.PairedPartners, _config.OwnedDolls, peerKey);
        var unlocked = _config.UnlockOwnerSettingsIfNoOwners();
        if (local.Any || unlocked)
            _config.Save();

        ClearPresence(peerKey);
        ClearOwnerSettings(peerKey);
    }

    private void HandleOwnerGrant(Envelope envelope)
    {
        var payload = envelope.GetPayload<OwnerGrantPayload>();
        if (payload is null)
            return;

        var dollKey = PairingKeyUtil.Normalize(payload.From);
        if (!PairingKeyUtil.IsValid(dollKey))
            return;

        // Accept grant only from a paired doll (or debug self).
        var fromSelf = string.Equals(dollKey, _config.PairingKey, StringComparison.Ordinal);
        if (!(_config.IsDebugEnabled && fromSelf) && !_consent.IsPairedByKey(dollKey))
            return;

        // 03-D11: a remote label, sanitised before it is stored.
        ApplyLocalOwnerGrant(dollKey, RemoteText.Sanitize(payload.Identity, WireLimits.ShortField));
    }

    private void HandleOwnerRevoked(Envelope envelope)
    {
        var payload = envelope.GetPayload<OwnerRevokedPayload>();
        if (payload is null)
            return;

        var dollKey = PairingKeyUtil.Normalize(payload.From);
        if (!PairingKeyUtil.IsValid(dollKey))
            return;

        var label = GetPairMessageLabel(dollKey);
        if (!_config.RemoveOwnedDoll(dollKey))
            return;

        _config.Save();
        ClearOwnerSettings(dollKey);
        PluginChat.Print(_chat, $"Your ownership of {label} was cleared.", PluginChat.Grey);
    }

    private async Task HandleInboundOwnerSettingsQueryAsync(Envelope envelope, CancellationToken ct)
    {
        var payload = envelope.GetPayload<OwnerSettingsQueryPayload>();
        if (payload is null)
            return;

        var fromKey = PairingKeyUtil.Normalize(payload.From);
        if (!PairingKeyUtil.IsValid(fromKey))
            return;

        if (!_consent.IsPairedByKey(fromKey))
            return;

        if (!_config.IsDoll || !_consent.IsOwnerKey(fromKey))
        {
            await SendEnvelopeAsync(Envelope.Create(MessageTypes.Error, new ErrorPayload
            {
                RequestId = payload.RequestId,
                To = fromKey,
                Code = ErrorCodes.NotAllowed,
                Message = GenericOwnerSettingsFailure,
            }), ct).ConfigureAwait(false);
            return;
        }

        var emotes = SnapshotUnlockedEmotes();

        await SendEnvelopeAsync(Envelope.Create(MessageTypes.OwnerSettingsResult, new OwnerSettingsResultPayload
        {
            RequestId = payload.RequestId,
            From = _config.PairingKey,
            To = fromKey,
            MaxWindHours = _config.MaxWindHours,
            AutoGroundSit = _config.AutoGroundSit,
            LockEmoteId = _config.EffectiveLockEmoteId,
            SettingsLocked = _config.OwnerSettingsLocked,
            // 03-D13: at most 512, names within the field limit, so the relay never drops the reply.
            Emotes = RelayWireRules.OutgoingEmotes(emotes),
            CanCall = _consent.CanReceiveCallFromKey(fromKey),
            TravelReady = TravelPluginsReady(),
        }), ct).ConfigureAwait(false);
    }

    private bool TravelPluginsReady() => _callTravel?.IsTravelReady ?? false;

    private async Task HandleInboundCallAsync(Envelope envelope, CancellationToken ct)
    {
        var payload = envelope.GetPayload<CallPayload>();
        if (payload is null)
            return;

        var fromKey = PairingKeyUtil.Normalize(payload.From);
        if (!PairingKeyUtil.IsValid(fromKey))
            return;

        if (!_consent.IsPairedByKey(fromKey))
            return;

        if (!_config.IsDoll || !_consent.CanReceiveCallFromKey(fromKey))
        {
            await SendEnvelopeAsync(Envelope.Create(MessageTypes.Error, new ErrorPayload
            {
                RequestId = payload.RequestId,
                To = fromKey,
                Code = ErrorCodes.NotAllowed,
                Message = "Unable to call that doll.",
            }), ct).ConfigureAwait(false);
            return;
        }

        if (await RefuseIfSafewordPausedAsync(envelope.Type, payload.RequestId, fromKey, ct).ConfigureAwait(false))
            return;

        if (_callTravel is null)
        {
            await SendEnvelopeAsync(Envelope.Create(MessageTypes.Error, new ErrorPayload
            {
                RequestId = payload.RequestId,
                To = fromKey,
                Code = ErrorCodes.TravelUnavailable,
                Message = "Call travel is unavailable.",
            }), ct).ConfigureAwait(false);
            return;
        }

        await _callTravel.RequestAsync(payload).ConfigureAwait(false);
    }

    private void HandleCallAck(Envelope envelope)
    {
        // Intentionally silent: a Call never echoes status to the owner.
        _ = envelope;
    }

    private void HandleCallResult(Envelope envelope)
    {
        var payload = envelope.GetPayload<CallResultPayload>();
        if (payload is null)
            return;

        // A reply to none of our pending Calls (or one already pruned, 07-A3) stays silent.
        if (!TryTakePendingCall(payload.RequestId))
            return;

        var dollKey = PairingKeyUtil.Normalize(payload.From);
        if (!PairingKeyUtil.IsValid(dollKey))
            return;

        // 07-B1 (D4): "<label> declined your Call."; every other status keeps its silence.
        if (CallResultNotice.ForOwner(payload.Status, GetPairMessageLabel(dollKey)) is { } notice)
            PluginChat.Print(_chat, notice, PluginChat.Yellow);
    }

    private bool TryTakePendingCall(string? requestId) => _pendingCalls.TryTake(requestId);

    private async Task HandleInboundOwnerSettingsUpdateAsync(Envelope envelope, CancellationToken ct)
    {
        var payload = envelope.GetPayload<OwnerSettingsUpdatePayload>();
        if (payload is null)
            return;

        var fromKey = PairingKeyUtil.Normalize(payload.From);
        if (!PairingKeyUtil.IsValid(fromKey))
            return;

        if (!_consent.IsPairedByKey(fromKey))
            return;

        if (!_config.IsDoll || !_consent.IsOwnerKey(fromKey))
        {
            await SendEnvelopeAsync(Envelope.Create(MessageTypes.Error, new ErrorPayload
            {
                RequestId = payload.RequestId,
                To = fromKey,
                Code = ErrorCodes.NotAllowed,
                Message = GenericOwnerSettingsFailure,
            }), ct).ConfigureAwait(false);
            return;
        }

        if (await RefuseIfSafewordPausedAsync(envelope.Type, payload.RequestId, fromKey, ct).ConfigureAwait(false))
            return;

        // 06-A1: these are the active character's own settings (Save writes them to its profile only), and 06-A9 makes
        // sure this message arrived for the active character.
        if (payload.MaxWindHours is { } hours)
            _config.MaxWindHours = Math.Clamp(Math.Round(hours), 1, 168);

        if (payload.AutoGroundSit is { } autoSit)
            _config.AutoGroundSit = autoSit;

        // 06-C6: an old owner client may still name an excluded emote (Doze): it is applied as Ground Sit, not refused.
        if (payload.LockEmoteId is { } emoteId)
            _config.LockEmoteId = LockEmoteRule.FromRemote(emoteId, _commands.IsAllowedLockEmote(emoteId));

        if (payload.SettingsLocked is { } locked)
            _config.OwnerSettingsLocked = locked;

        _config.Save();

        await SendEnvelopeAsync(Envelope.Create(MessageTypes.OwnerSettingsAck, new OwnerSettingsAckPayload
        {
            RequestId = payload.RequestId,
            From = _config.PairingKey,
            To = fromKey,
            MaxWindHours = _config.MaxWindHours,
            AutoGroundSit = _config.AutoGroundSit,
            LockEmoteId = _config.EffectiveLockEmoteId,
            SettingsLocked = _config.OwnerSettingsLocked,
        }), ct).ConfigureAwait(false);
    }

    private void HandleOwnerSettingsResult(Envelope envelope)
    {
        var payload = envelope.GetPayload<OwnerSettingsResultPayload>();
        if (payload is null)
            return;

        var dollKey = PairingKeyUtil.Normalize(payload.From);
        if (!PairingKeyUtil.IsValid(dollKey))
            return;

        TryTakePendingOwnerRequest(payload.RequestId, out _);

        lock (_ownerSettingsLock)
        {
            // 08-T6: the result of the re-query after a refusal keeps the "paused" message, and a new RestoreSeq makes
            // the Owner window take these values into its draft. 08-D17: only the re-query's own result (by RequestId);
            // any result replaces the snapshot, so it also clears the flag.
            _ownerSettingsByDollKey.TryGetValue(dollKey, out var previous);
            var keepError = RelaySyncRules.IsOwnerSettingsReQueryResult(
                previous is { KeepErrorOnNextResult: true }, previous?.ReQueryRequestId, payload.RequestId);
            _ownerSettingsByDollKey[dollKey] = new OwnerSettingsSnapshot
            {
                DollKey = dollKey,
                MaxWindHours = payload.MaxWindHours,
                AutoGroundSit = payload.AutoGroundSit,
                LockEmoteId = payload.LockEmoteId == 0 ? CharacterProfile.GroundSitEmoteId : payload.LockEmoteId,
                SettingsLocked = payload.SettingsLocked,
                // 03-D12: named from this client's own Emote sheet by ID; the doll's names only as a sanitised fallback.
                Emotes = RelayWireRules.ResolveOwnerEmotes(payload.Emotes, _commands.GetEmoteName),
                CanCall = payload.CanCall,
                TravelReady = payload.TravelReady,
                LastError = RelaySyncRules.OwnerSettingsErrorAfterResult(previous?.LastError, keepError),
                HasData = true,
                RestoreSeq = keepError ? ++_ownerSettingsRestoreSeq : 0,
            };
        }
    }

    private void HandleOwnerSettingsAck(Envelope envelope)
    {
        var payload = envelope.GetPayload<OwnerSettingsAckPayload>();
        if (payload is null)
            return;

        var dollKey = PairingKeyUtil.Normalize(payload.From);
        if (!PairingKeyUtil.IsValid(dollKey))
            return;

        TryTakePendingOwnerRequest(payload.RequestId, out _, out var notify);

        lock (_ownerSettingsLock)
        {
            if (!_ownerSettingsByDollKey.TryGetValue(dollKey, out var existing))
            {
                existing = new OwnerSettingsSnapshot { DollKey = dollKey };
                _ownerSettingsByDollKey[dollKey] = existing;
            }

            existing.MaxWindHours = payload.MaxWindHours;
            existing.AutoGroundSit = payload.AutoGroundSit;
            existing.LockEmoteId = payload.LockEmoteId == 0 ? CharacterProfile.GroundSitEmoteId : payload.LockEmoteId;
            existing.SettingsLocked = payload.SettingsLocked;
            existing.LastError = null;
            existing.KeepErrorOnNextResult = false;
            existing.ReQueryRequestId = null;
            existing.HasData = true;
        }

        if (notify)
            PluginChat.Print(
                _chat,
                $"Doll settings updated for {GetPairMessageLabel(dollKey)}.",
                PluginChat.Green);
    }

    private void RememberPendingOwnerRequest(string requestId, string dollKey, bool notifyOnAck = false)
    {
        lock (_ownerSettingsLock)
            _pendingOwnerRequests[requestId] = (dollKey, notifyOnAck);
    }

    private bool TryTakePendingOwnerRequest(string? requestId, out string dollKey)
        => TryTakePendingOwnerRequest(requestId, out dollKey, out _);

    private bool TryTakePendingOwnerRequest(string? requestId, out string dollKey, out bool notifyOnAck)
    {
        dollKey = string.Empty;
        notifyOnAck = false;
        if (string.IsNullOrEmpty(requestId))
            return false;

        lock (_ownerSettingsLock)
        {
            if (!_pendingOwnerRequests.Remove(requestId, out var pending))
                return false;
            dollKey = pending.DollKey;
            notifyOnAck = pending.NotifyOnAck;
            return true;
        }
    }

    private void EnsureOwnerSettingsSlot(string dollKey)
    {
        lock (_ownerSettingsLock)
        {
            if (_ownerSettingsByDollKey.ContainsKey(dollKey))
                return;
            _ownerSettingsByDollKey[dollKey] = new OwnerSettingsSnapshot { DollKey = dollKey };
        }
    }

    private void SetOwnerSettingsError(string dollKey, string message)
    {
        lock (_ownerSettingsLock)
        {
            if (!_ownerSettingsByDollKey.TryGetValue(dollKey, out var snap))
            {
                snap = new OwnerSettingsSnapshot { DollKey = dollKey };
                _ownerSettingsByDollKey[dollKey] = snap;
            }

            snap.LastError = message;
            // 08-T6: a newer error replaces the "paused" message; the next result doesn't keep it.
            snap.KeepErrorOnNextResult = false;
            snap.ReQueryRequestId = null;
        }
    }

    /// <summary>
    /// 06-B3: the doll refused a settings change because her safeword paused remote control. Only a result or an ack
    /// changes the snapshot, so it still holds the last confirmed values; a new <see cref="OwnerSettingsSnapshot.RestoreSeq"/>
    /// makes the Owner window drop the refused values from its draft and show these again.
    /// 08-T6: <paramref name="keepErrorOnNextResult"/> when a re-query follows, so its result keeps the message.
    /// </summary>
    private void RestoreOwnerSettingsAfterRefusal(string dollKey, string message, bool keepErrorOnNextResult = false)
    {
        lock (_ownerSettingsLock)
        {
            if (!_ownerSettingsByDollKey.TryGetValue(dollKey, out var snap))
            {
                snap = new OwnerSettingsSnapshot { DollKey = dollKey };
                _ownerSettingsByDollKey[dollKey] = snap;
            }

            snap.LastError = message;
            snap.RestoreSeq = ++_ownerSettingsRestoreSeq;
            snap.KeepErrorOnNextResult = keepErrorOnNextResult;
            // 08-D17: a re-query already waiting keeps its RequestId; a new one records its own when it's sent.
            if (!keepErrorOnNextResult)
                snap.ReQueryRequestId = null;
        }
    }

    /// <summary>08-D17: the re-query about to be sent; only its result keeps the "paused" message.</summary>
    private void SetOwnerSettingsReQuery(string dollKey, string requestId)
    {
        lock (_ownerSettingsLock)
        {
            if (_ownerSettingsByDollKey.TryGetValue(dollKey, out var snap) && snap.KeepErrorOnNextResult)
                snap.ReQueryRequestId = requestId;
        }
    }

    /// <summary>
    /// 08-D17: the re-query <paramref name="requestId"/> wasn't sent, so no result will clear the flag: clear it here,
    /// with the one-at-a-time guard it is (<see cref="OwnerSettingsReQueryPending"/>). The "paused" message stays.
    /// Nothing changes when a newer error, ack or result already replaced it.
    /// </summary>
    private void ClearOwnerSettingsReQuery(string dollKey, string requestId)
    {
        lock (_ownerSettingsLock)
        {
            if (!_ownerSettingsByDollKey.TryGetValue(dollKey, out var snap)
                || !string.Equals(snap.ReQueryRequestId, requestId, StringComparison.Ordinal))
                return;

            snap.KeepErrorOnNextResult = false;
            snap.ReQueryRequestId = null;
        }
    }

    /// <summary>08-T6: whether a re-query after an earlier refusal is still waiting for its result.</summary>
    private bool OwnerSettingsReQueryPending(string dollKey)
    {
        lock (_ownerSettingsLock)
            return _ownerSettingsByDollKey.TryGetValue(dollKey, out var snap) && snap.KeepErrorOnNextResult;
    }

    private void ClearOwnerSettings(string dollKey)
    {
        lock (_ownerSettingsLock)
            _ownerSettingsByDollKey.Remove(dollKey);
    }

    private void ClearAllOwnerSettings()
    {
        lock (_ownerSettingsLock)
        {
            _ownerSettingsByDollKey.Clear();
            _pendingOwnerRequests.Clear();
        }
    }

    private static OwnerSettingsSnapshot CloneOwnerSettings(OwnerSettingsSnapshot snap) =>
        new()
        {
            DollKey = snap.DollKey,
            MaxWindHours = snap.MaxWindHours,
            AutoGroundSit = snap.AutoGroundSit,
            LockEmoteId = snap.LockEmoteId,
            SettingsLocked = snap.SettingsLocked,
            Emotes = snap.Emotes.ToList(),
            CanCall = snap.CanCall,
            TravelReady = snap.TravelReady,
            LastError = snap.LastError,
            HasData = snap.HasData,
            RestoreSeq = snap.RestoreSeq,
        };

    private void RefreshUnlockedEmoteCache()
    {
        if (!_loggedIn || !_config.IsDoll)
            return;

        try
        {
            var list = _commands.GetUnlockedLoopingEmotes()
                .Select(e => new OwnerEmoteInfo { Id = e.Id, Name = e.Name })
                .ToList();
            lock (_emoteCacheLock)
                _cachedUnlockedEmotes = list;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "WindUpKey failed refreshing unlocked emote cache");
        }
    }

    private List<OwnerEmoteInfo> SnapshotUnlockedEmotes()
    {
        lock (_emoteCacheLock)
        {
            return _cachedUnlockedEmotes
                .Select(e => new OwnerEmoteInfo { Id = e.Id, Name = e.Name })
                .ToList();
        }
    }

    private void SetPresence(string peerKey, PartnerPresence status)
    {
        lock (_presenceLock)
            _presenceByKey[peerKey] = status;
    }

    private void ClearPresence(string peerKey)
    {
        lock (_presenceLock)
        {
            _presenceByKey.Remove(peerKey);
            _presenceLastQueryUtc.Remove(peerKey);
        }
    }

    private void ClearAllPresence()
    {
        lock (_presenceLock)
        {
            _presenceByKey.Clear();
            _presenceLastQueryUtc.Clear();
        }
    }

    /// <summary>
    /// True when the frame was sent; false when there is no open socket, or the message is over the relay's limit (it's
    /// logged and dropped). A failed send throws.
    /// </summary>
    private async Task<bool> SendEnvelopeAsync(Envelope envelope, CancellationToken ct)
    {
        ClientWebSocket? socket;
        lock (_socketGate)
            socket = _socket;

        if (socket is not { State: WebSocketState.Open })
            return false;

        // 03-D13: never more than the relay reads, so a new client can't trip its 1009 close.
        if (!RelayWireRules.TryEncodeForSend(envelope, out var bytes))
        {
            _log.Warning("WindUpKey did not send a {Type} of {Bytes} bytes: over the relay's message limit", envelope.Type, bytes.Length);
            return false;
        }

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void CloseSocket()
    {
        ClientWebSocket? socket;
        lock (_socketGate)
        {
            socket = _socket;
            _socket = null;
            _socketGeneration = 0;
        }

        try
        {
            socket?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "WindUpKey CloseSocket");
        }
    }

    private void CloseSocketForGeneration(int generation)
    {
        ClientWebSocket? socket;
        lock (_socketGate)
        {
            if (_socketGeneration != generation)
                return;

            socket = _socket;
            _socket = null;
            _socketGeneration = 0;
        }

        try
        {
            socket?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "WindUpKey CloseSocket generation={Generation}", generation);
        }
    }

#if WINDUP_TESTING
    // ---------- 04 self-test accessors (Testing builds only, 04-D1) ----------

    /// <summary>04-D6: installs the self-test's tap, or removes it with null.</summary>
    internal void SetSelfTestTap(SelfTestTap? tap) => _selfTestTap = tap;

    /// <summary>
    /// 04-D7 outage: holds the connect loop (memory only, never persisted) and closes the socket, or lifts the hold.
    /// Once lifted, a running connect loop connects again on its next pass, so the outage lifts it in the same framework
    /// tick as the Reconnect path's <see cref="Start"/> (04-D12).
    /// </summary>
    internal void SetSelfTestOutageHold(bool hold)
    {
        _selfTestOutageHold = hold;
        if (hold)
            CloseSocket();
    }

    internal int AcceptedConnections => Volatile.Read(ref _acceptedConnections);

    internal int SessionReplacedSeen => Volatile.Read(ref _sessionReplacedSeen);

    internal int CloseCount => Volatile.Read(ref _closeCount);

    internal WebSocketCloseStatus? LastCloseStatus =>
        Volatile.Read(ref _lastCloseStatus) is var status and >= 0 ? (WebSocketCloseStatus)status : null;

    internal string? LastCloseDescription => _lastCloseDescription;

    /// <summary>The live connection's host URL while connected (read-only), else null.</summary>
    internal string? ConnectedRelayUrl => IsConnected ? _connectionUrl : null;

    /// <summary>True while connected and this connection's register was accepted.</summary>
    internal bool RegisterAccepted => _registerAccepted && IsConnected;

    /// <summary>The connect-policy epoch: login, Reconnect, character switch, import and revert change it.</summary>
    internal int ConnectEpoch => _connectPolicy.Epoch;

    /// <summary>The auth block's code while blocked, else null.</summary>
    internal string? AuthBlockCode => _authBlockStatus is null ? null : _connectPolicy.BlockCode;

    /// <summary>This connection's pairSync <c>peerKeys</c>, or null before it arrived.</summary>
    internal IReadOnlyList<string>? LastPeerKeys => _lastPeerKeys;

    /// <summary>
    /// 04-D11: the key the live session registered with (recorded when the register was sent), while that register is
    /// accepted; else null. The own-key probes address this key, never the current <c>config.PairingKey</c>.
    /// </summary>
    internal string? RegisteredKey => RegisterAccepted ? _selfTestRegisteredKey : null;

    private void RecordSelfTestClose(WebSocketCloseStatus? status, string? description)
    {
        _lastCloseDescription = description;
        Volatile.Write(ref _lastCloseStatus, status is { } s ? (int)s : -1);
        Interlocked.Increment(ref _closeCount);
    }

    /// <summary>
    /// 04-D6: the self-test's send. The same socket and send lock as every send, within the 64 KiB cap, and it never
    /// enters the pending wind, wind-request, owner or call bookkeeping. Throws as a send does.
    /// </summary>
    internal Task<bool> SendSelfTestAsync(Envelope envelope, CancellationToken ct) => SendEnvelopeAsync(envelope, ct);

    /// <summary>04-D7 oversize only: sends these bytes as one text message, skipping the 03-D13 send cap. Throws as a send does.</summary>
    internal async Task<bool> SendSelfTestRawAsync(byte[] utf8, CancellationToken ct)
    {
        ClientWebSocket? socket;
        lock (_socketGate)
            socket = _socket;
        if (socket is not { State: WebSocketState.Open })
            return false;

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(utf8, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// 04-D4: the identity store's values-free view of this character, compared against Dalamud's
    /// <paramref name="pluginConfigDirectory"/> (04-D10). Never takes the identity-file lock. Framework thread.
    /// </summary>
    internal RelayIdentityDiagnosis? DiagnoseRelayIdentity(string pluginConfigDirectory) =>
        string.IsNullOrEmpty(_config.ActiveContentId)
            ? null
            : _identityStore.Diagnose(_config.ActiveContentId, PairingKeyUtil.FromContentIdHex(_config.ActiveContentId), pluginConfigDirectory);

    /// <summary>04-D5: whether a probe's candidate secret is this character's current or previous one. Framework thread.</summary>
    internal bool IsKnownRelaySecret(string candidate) =>
        !string.IsNullOrEmpty(_config.ActiveContentId) && _identityStore.IsKnownSecret(_config.ActiveContentId, candidate);

    /// <summary>04-D3: this character's secrets and their hashes, for the report's scrubber only. Framework thread. Never log.</summary>
    internal IReadOnlyList<string> SelfTestSensitiveValues() =>
        string.IsNullOrEmpty(_config.ActiveContentId) ? [] : _identityStore.SensitiveValues(_config.ActiveContentId);

    /// <summary>
    /// 01-4: what importing the current value would do (expected Unchanged), without changing anything. The real
    /// <see cref="CheckRelaySecretImport"/> path, which re-reads the file under its lock: once per run (04-D9). Framework thread.
    /// </summary>
    internal RelayIdentityImport? CheckCurrentRelayIdentityImport()
    {
        var current = ActiveRelaySecret;
        return string.IsNullOrEmpty(current) ? null : CheckRelaySecretImport(current);
    }
#endif

    /// <summary>
    /// Never blocks the framework thread (08-D6): no wait on the loop task or a send. Ticks queued before this check
    /// <see cref="_disposed"/> and do nothing.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        _timer.DollWindChanged = null;
        Stop();
        // 08-P2 (K3a): _sendLock isn't disposed, so a send still finishing in the background can release it without an
        // ObjectDisposedException. A SemaphoreSlim whose AvailableWaitHandle is never read holds nothing unmanaged.
    }
}
