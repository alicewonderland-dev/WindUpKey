using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using WindUpKey.Protocol;

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
    public List<(ushort Id, string Name)> Emotes { get; set; } = [];
    public string? LastError { get; set; }
    public bool HasData { get; set; }
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
    private static readonly TimeSpan PresenceThrottle = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long the relay must stay unreachable (while logged in with identity) before
    /// unwound movement/teleport locks are suspended as a safety fallback.
    /// </summary>
    public static readonly TimeSpan MovementLockSafetyGrace = TimeSpan.FromSeconds(60);

    private readonly Configuration _config;
    private readonly IClientState _clientState;
    private readonly IObjectTable _objectTable;
    private readonly IPluginLog _log;
    private readonly IChatGui _chat;
    private readonly ConsentService _consent;
    private readonly WindTimerService _timer;
    private readonly IWindNotifier _notifier;
    private readonly SoundEffectService _sounds;
    private readonly GameCommandRunner _commands;

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _reconnectDelayMs = 1000;
    private string? _lastStatus;
    private int _running;
    private DateTimeOffset? _unreachableSinceUtc;

    private readonly object _presenceLock = new();
    private readonly Dictionary<string, PartnerPresence> _presenceByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _presenceLastQueryUtc = new(StringComparer.Ordinal);

    /// <summary>Pending wind requestId → hours sent (winder SFX on windResult).</summary>
    private readonly object _pendingWindLock = new();
    private readonly Dictionary<string, double> _pendingWindHoursByRequestId = new(StringComparer.Ordinal);

    private readonly object _ownerSettingsLock = new();
    private readonly Dictionary<string, OwnerSettingsSnapshot> _ownerSettingsByDollKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string DollKey, bool NotifyOnAck)> _pendingOwnerRequests = new(StringComparer.Ordinal);
    private readonly object _emoteCacheLock = new();
    private List<OwnerEmoteInfo> _cachedUnlockedEmotes = [];

    // Written on framework thread, read by background loop — never touch Dalamud APIs off-thread.
    private volatile bool _loggedIn;
    private volatile string? _cachedIdentity;
    private int _tickLogCounter;

    public RelayClient(
        Configuration config,
        IClientState clientState,
        IObjectTable objectTable,
        IPluginLog log,
        IChatGui chat,
        ConsentService consent,
        WindTimerService timer,
        IWindNotifier notifier,
        SoundEffectService sounds,
        GameCommandRunner commands)
    {
        _config = config;
        _clientState = clientState;
        _objectTable = objectTable;
        _log = log;
        _chat = chat;
        _consent = consent;
        _timer = timer;
        _notifier = notifier;
        _sounds = sounds;
        _commands = commands;
    }

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    /// <summary>Short offline hint for the config UI. Never includes URL or token.</summary>
    public string? LastStatus => _lastStatus;

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

        _ = RequestPresenceAsync(key);
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
            _cachedIdentity = _loggedIn ? TryReadIdentity() : null;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "WindUpKey Tick: failed reading client state");
            _loggedIn = false;
            _cachedIdentity = null;
            _lastStatus = "Error reading character state (see dalamud.log).";
            UpdateUnreachableTracking();
            return;
        }

        if (_loggedIn)
        {
            try
            {
                // Skip ContentId / profile swap until LocalPlayer exists (zone load).
                if (_objectTable.LocalPlayer is not null)
                {
                    var contentId = TryReadLocalContentId();
                    if (contentId != 0 && _config.ActivateCharacter(contentId))
                    {
                        ClearAllPresence();
                        ClearAllOwnerSettings();
                        _config.Save();
                        _log.Information("WindUpKey active character profile={ContentId}", _config.ActiveContentId);
                        if (IsConnected)
                            CloseSocket();
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "WindUpKey Tick: ActivateCharacter failed");
            }
        }

        SyncPairingKeyFromIdentity();
        UpdateUnreachableTracking();

        if (Interlocked.Increment(ref _tickLogCounter) % 300 == 1)
        {
            RefreshUnlockedEmoteCache();
            _log.Debug(
                "WindUpKey Tick: running={Running} loggedIn={LoggedIn} identity={Identity} connected={Connected} status={Status}",
                Volatile.Read(ref _running) == 1,
                _loggedIn,
                _cachedIdentity ?? "(none)",
                IsConnected,
                _lastStatus ?? "(null)");
        }
    }

    /// <summary>
    /// Counts continuous "should be connected but isn't" time for the movement-lock safety fallback.
    /// Title screen / missing identity do not count — those are not a failed host connection.
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
    /// Keeps <see cref="Configuration.PairingKey"/> equal to the identity-derived code.
    /// Safe to call on the framework thread (may Save).
    /// </summary>
    private void SyncPairingKeyFromIdentity()
    {
        if (_cachedIdentity is null)
            return;

        var derived = PairingKeyUtil.FromIdentity(_cachedIdentity);
        if (!PairingKeyUtil.IsValid(derived))
            return;

        _config.LastKnownIdentity = _cachedIdentity;

        if (string.Equals(_config.PairingKey, derived, StringComparison.Ordinal))
            return;

        var previous = _config.PairingKey;
        _config.PairingKey = derived;

        if (PairingKeyUtil.IsValid(previous))
        {
            MigrateSelfPairKey(previous, derived);
            EnqueueRotationsForPartners(previous, derived, _cachedIdentity);
            PluginChat.Print(
                _chat,
                $"Pairing key changed to {derived} (was {previous}). " +
                "Paired partners will be notified when online; they can also update your key manually.",
                PluginChat.Yellow);
        }

        _config.Save();
        _log.Information(
            "WindUpKey pairing key synced from identity (was {Previous})",
            PairingKeyUtil.IsValid(previous) ? previous : "(none)");

        // Re-register if we were already online under a different key.
        if (IsConnected)
            CloseSocket();
    }

    private void MigrateSelfPairKey(string oldKey, string newKey)
    {
        var selfPair = _config.FindPairByKey(oldKey);
        if (selfPair is null)
            return;
        if (string.Equals(PairingKeyUtil.Normalize(selfPair.PartnerKey), newKey, StringComparison.Ordinal))
            return;
        selfPair.PartnerKey = newKey;
    }

    private void EnqueueRotationsForPartners(string oldKey, string newKey, string identity)
    {
        foreach (var partner in _config.PairedPartners.ToArray())
        {
            var peer = PairingKeyUtil.Normalize(partner.PartnerKey);
            if (!PairingKeyUtil.IsValid(peer))
                continue;
            if (string.Equals(peer, newKey, StringComparison.Ordinal)
                || string.Equals(peer, oldKey, StringComparison.Ordinal))
                continue;

            _config.EnqueueKeyRotation(peer, oldKey, newKey, identity);
        }
    }

    public void Start()
    {
        _log.Information("WindUpKey relay Start()");
        Stop();

        _lastStatus = "Starting…";
        _reconnectDelayMs = 1000;
        _loopCts = new CancellationTokenSource();
        var ct = _loopCts.Token;
        Interlocked.Exchange(ref _running, 1);

        _loopTask = Task.Run(async () =>
        {
            _log.Debug("WindUpKey relay background loop entered");
            try
            {
                await RunLoopAsync(ct).ConfigureAwait(false);
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
                Interlocked.Exchange(ref _running, 0);
            }
        });
    }

    public void Stop()
    {
        _log.Debug("WindUpKey relay Stop()");
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
        await SendEnvelopeAsync(Envelope.Create(MessageTypes.Wind, payload), CancellationToken.None);
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

        await SendEnvelopeAsync(Envelope.Create(MessageTypes.Unwind, payload), CancellationToken.None);
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
                    _log.Information("WindUpKey paired with key={Key}", theirKey);
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

        if (!_config.PendingPartnerKeys.Contains(theirKey, StringComparer.Ordinal))
        {
            _config.PendingPartnerKeys.Add(theirKey);
            _config.Save();
        }

        var payload = new PairSubmitPayload
        {
            RequestId = Guid.NewGuid().ToString("N"),
            MyKey = _config.PairingKey,
            TheirKey = theirKey,
        };
        await SendEnvelopeAsync(Envelope.Create(MessageTypes.PairSubmit, payload), CancellationToken.None);
    }

    public async Task UnpairByKeyAsync(string partnerKey)
    {
        var peerKey = PairingKeyUtil.Normalize(partnerKey);
        if (!PairingKeyUtil.IsValid(peerKey))
            return;

        var wasOwner = _config.FindPairByKey(peerKey) is { IsOwner: true };

        _config.PairedPartners.RemoveAll(p =>
            string.Equals(PairingKeyUtil.Normalize(p.PartnerKey), peerKey, StringComparison.Ordinal));
        _config.Save();
        ClearPresence(peerKey);

        // Self-pair is local only — nothing to notify on the relay.
        if (!IsConnected || string.Equals(peerKey, _config.PairingKey, StringComparison.Ordinal))
            return;

        if (wasOwner)
            await SendOwnerRevokedAsync(peerKey).ConfigureAwait(false);

        await SendEnvelopeAsync(Envelope.Create(MessageTypes.PairRemove, new PairRemovePayload
        {
            PeerKey = peerKey,
        }), CancellationToken.None);
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
            _config.Save();
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
            partner.IsOwner = false;

        _config.OwnerSettingsLocked = false;

        // Self-owner lives on this same client — clear OwnedDolls locally (no useful relay revoke).
        var selfKey = PairingKeyUtil.Normalize(_config.PairingKey);
        if (PairingKeyUtil.IsValid(selfKey) && ownerKeys.Contains(selfKey, StringComparer.Ordinal))
        {
            _config.RemoveOwnedDoll(selfKey);
            ClearOwnerSettings(selfKey);
        }

        _config.Save();

        if (!IsConnected)
            return;

        foreach (var key in ownerKeys)
        {
            if (string.Equals(key, selfKey, StringComparison.Ordinal))
                continue;
            await SendOwnerRevokedAsync(key).ConfigureAwait(false);
        }
    }

    public async Task QueryOwnerSettingsAsync(string dollKey)
    {
        var key = PairingKeyUtil.Normalize(dollKey);
        if (!PairingKeyUtil.IsValid(key))
            return;

        EnsureOwnerSettingsSlot(key);

        // Debug self-owner: answer from local config (relay self-route is unreliable for UI testing).
        if (_config.IsDebugEnabled
            && string.Equals(key, PairingKeyUtil.Normalize(_config.PairingKey), StringComparison.Ordinal))
        {
            ApplyLocalOwnerSettingsSnapshot(key);
            return;
        }

        if (!IsConnected)
        {
            SetOwnerSettingsError(key, "Not connected yet. Try again in a moment.");
            return;
        }

        var requestId = Guid.NewGuid().ToString("N");
        RememberPendingOwnerRequest(requestId, key);

        await SendEnvelopeAsync(Envelope.Create(MessageTypes.OwnerSettingsQuery, new OwnerSettingsQueryPayload
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
            ApplyLocalOwnerSettingsUpdate(maxWindHours, autoGroundSit, lockEmoteId, settingsLocked);
            ApplyLocalOwnerSettingsSnapshot(key);
            if (notify)
                PluginChat.Print(_chat, "Doll settings updated.", PluginChat.Green);
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
        _config.UpsertOwnedDoll(dollKey, identity);
        _config.Save();
        PluginChat.Print(_chat, "You were designated as an owner.", PluginChat.Green);
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

        if (lockEmoteId is { } emoteId)
            _config.LockEmoteId = emoteId == 0 ? (ushort)52 : emoteId;

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
                LastError = null,
                HasData = true,
            };
        }
    }

    private async Task SendOwnerRevokedAsync(string ownerKey)
    {
        await SendEnvelopeAsync(Envelope.Create(MessageTypes.OwnerRevoked, new OwnerRevokedPayload
        {
            From = _config.PairingKey,
            To = ownerKey,
        }), CancellationToken.None).ConfigureAwait(false);
    }

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

    private async Task RunLoopAsync(CancellationToken ct)
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

            try
            {
                _lastStatus = "Connecting…";
                _log.Information("WindUpKey loop: connecting as {Identity}", identity);
                await ConnectAndRegisterAsync(ct).ConfigureAwait(false);
                _lastStatus = null;
                _reconnectDelayMs = 1000;
                await ReceiveLoopAsync(ct).ConfigureAwait(false);
                _lastStatus = "Disconnected. Reconnecting…";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _lastStatus = "Cannot reach the host. Is Wind-Up Key Host running?";
                _log.Warning(ex, "WindUpKey relay connection issue");
            }

            CloseSocket();
            try
            {
                await Task.Delay(_reconnectDelayMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            _reconnectDelayMs = Math.Min(_reconnectDelayMs * 2, 30_000);
        }

        _log.Debug("WindUpKey RunLoopAsync end");
    }

    private async Task ConnectAndRegisterAsync(CancellationToken ct)
    {
        CloseSocket();
        _config.ApplyRelayDefaults();

        // Identity is required by RunLoopAsync before connect; derive without Save (off framework thread).
        var identity = _cachedIdentity;
        if (identity is not null)
        {
            var derived = PairingKeyUtil.FromIdentity(identity);
            if (PairingKeyUtil.IsValid(derived))
                _config.PairingKey = derived;
        }

        if (!PairingKeyUtil.IsValid(_config.PairingKey))
            throw new InvalidOperationException("Pairing key unavailable before register.");

        var baseUrl = _config.RelayUrl;
        var safeHost = SafeHost(baseUrl);
        _lastStatus = $"Connecting ({safeHost})…";
        _log.Information("WindUpKey connect attempt host={Host}", safeHost);

        var uri = BuildUri(baseUrl);
        _socket = new ClientWebSocket();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
        await _socket.ConnectAsync(uri, timeoutCts.Token).ConfigureAwait(false);
        _log.Information("WindUpKey WebSocket open host={Host} state={State}", safeHost, _socket.State);

        var register = new RegisterPayload
        {
            Token = string.IsNullOrEmpty(_config.RelayToken) ? null : _config.RelayToken,
            PairingKey = _config.PairingKey,
        };
        await SendEnvelopeAsync(Envelope.Create(MessageTypes.Register, register), ct).ConfigureAwait(false);
        _log.Information("WindUpKey registered on relay as key={Key}", _config.PairingKey);

        // Re-submit pending pair keys after reconnect.
        foreach (var key in _config.PendingPartnerKeys.ToArray())
        {
            await SendEnvelopeAsync(Envelope.Create(MessageTypes.PairSubmit, new PairSubmitPayload
            {
                RequestId = Guid.NewGuid().ToString("N"),
                MyKey = _config.PairingKey,
                TheirKey = key,
            }), ct).ConfigureAwait(false);
        }

        // Presence-driven keyRotated flush (send when peer answers online).
        foreach (var pending in _config.PendingKeyRotations.ToArray())
            await RequestPresenceAsync(pending.PartnerKey).ConfigureAwait(false);
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

    private Uri BuildUri(string baseUrl)
    {
        var url = baseUrl.Trim();
        if (!string.IsNullOrEmpty(_config.RelayToken))
        {
            var sep = url.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            url = $"{url}{sep}token={Uri.EscapeDataString(_config.RelayToken)}";
        }

        return new Uri(url);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (_socket is { State: WebSocketState.Open } && !ct.IsCancellationRequested)
        {
            var result = await _socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                break;

            if (result.MessageType != WebSocketMessageType.Text)
                continue;

            var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
            while (!result.EndOfMessage)
            {
                result = await _socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                json += Encoding.UTF8.GetString(buffer, 0, result.Count);
            }

            await HandleMessageAsync(json, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleMessageAsync(string json, CancellationToken ct)
    {
        var envelope = Envelope.TryParse(json);
        if (envelope is null)
            return;

        switch (envelope.Type)
        {
            case MessageTypes.Wind:
                await HandleInboundWindAsync(envelope, ct).ConfigureAwait(false);
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
            case MessageTypes.PairRemove:
                HandlePairRemove(envelope);
                break;
            case MessageTypes.PresenceQuery:
                await HandleInboundPresenceQueryAsync(envelope, ct).ConfigureAwait(false);
                break;
            case MessageTypes.PresenceResult:
                HandlePresenceResult(envelope);
                break;
            case MessageTypes.KeyRotated:
                HandleKeyRotated(envelope);
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
        var winderIdentity = pair is { Identity: { Length: > 0 } }
            ? pair.Identity
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

        var pair = _config.FindPairByKey(fromKey);
        var winderIdentity = pair is { Identity: { Length: > 0 } }
            ? pair.Identity
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
        var pair = _config.FindPairByKey(fromKey);
        var label = pair is { Identity: { Length: > 0 } }
            ? pair.Identity
            : fromKey;
        _notifier.NotifyWinderRemaining(label, remaining);
    }

    private async Task HandleErrorAsync(Envelope envelope, CancellationToken ct)
    {
        var payload = envelope.GetPayload<ErrorPayload>();
        if (payload is null)
            return;

        if (!string.IsNullOrEmpty(payload.RequestId))
        {
            TryTakePendingWind(payload.RequestId, out _);
            if (TryTakePendingOwnerRequest(payload.RequestId, out var dollKey))
            {
                var message = payload.Code == ErrorCodes.TargetOffline
                    ? "That doll is offline."
                    : payload.Code == ErrorCodes.NotAllowed
                        ? "Not allowed to manage that doll."
                        : GenericOwnerSettingsFailure;
                SetOwnerSettingsError(dollKey, message);
                _notifier.NotifyWinderError(message);
                await Task.CompletedTask.ConfigureAwait(false);
                return;
            }
        }

        if (payload.Code == ErrorCodes.PairKeyCollision)
        {
            // Key is identity-derived and must stay stable; another session (or stale socket) holds it.
            _log.Warning("WindUpKey pairing key in use — reconnecting without changing key");
            CloseSocket();
            return;
        }

        // keyRotated offline (no requestId) — keep PendingKeyRotations and retry on presence.
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
        if (!PairingKeyUtil.IsValid(peerKey))
            return;

        var existing = _config.FindPairByKey(peerKey);
        if (existing is null)
        {
            _config.PairedPartners.Add(new PairedPartner
            {
                Identity = string.Empty,
                PartnerKey = peerKey,
                CanWindMe = false,
            });
        }

        _config.PendingPartnerKeys.RemoveAll(k => string.Equals(k, peerKey, StringComparison.Ordinal));
        _config.Save();
        _log.Information("WindUpKey paired with key={Key}", peerKey);
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
            _ = SendOwnerRevokedAsync(peerKey);
    }

    private async Task HandleInboundPresenceQueryAsync(Envelope envelope, CancellationToken ct)
    {
        var payload = envelope.GetPayload<PresenceQueryPayload>();
        if (payload is null)
            return;

        var fromKey = PairingKeyUtil.Normalize(payload.From);
        if (!PairingKeyUtil.IsValid(fromKey) || !PairingKeyUtil.IsValid(_config.PairingKey))
            return;

        await SendEnvelopeAsync(Envelope.Create(MessageTypes.PresenceResult, new PresenceResultPayload
        {
            RequestId = payload.RequestId,
            From = _config.PairingKey,
            To = fromKey,
            Online = true,
            StillPaired = _consent.IsPairedByKey(fromKey),
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

        if (payload.StillPaired == false)
            SilentRemovePartner(peerKey);
        else if (payload.Online)
        {
            _ = TrySendPendingKeyRotationAsync(peerKey, CancellationToken.None);
            _ = TryResendOwnerGrantAsync(peerKey);
        }
    }

    private async Task TryResendOwnerGrantAsync(string partnerKey)
    {
        if (!_config.IsDoll || !IsConnected)
            return;

        var peer = PairingKeyUtil.Normalize(partnerKey);
        var pair = _config.FindPairByKey(peer);
        if (pair is not { IsOwner: true })
            return;

        if (string.Equals(peer, _config.PairingKey, StringComparison.Ordinal))
            return;

        await SendEnvelopeAsync(Envelope.Create(MessageTypes.OwnerGrant, new OwnerGrantPayload
        {
            From = _config.PairingKey,
            To = peer,
            Identity = string.IsNullOrWhiteSpace(_config.LastKnownIdentity)
                ? _cachedIdentity
                : _config.LastKnownIdentity,
        }), CancellationToken.None).ConfigureAwait(false);
    }

    private void HandleKeyRotated(Envelope envelope)
    {
        var payload = envelope.GetPayload<KeyRotatedPayload>();
        if (payload is null)
            return;

        var oldKey = PairingKeyUtil.Normalize(payload.OldKey);
        var newKey = PairingKeyUtil.Normalize(payload.NewKey);
        if (!PairingKeyUtil.IsValid(oldKey) || !PairingKeyUtil.IsValid(newKey))
            return;

        var ownedUpdated = _config.TryReplaceOwnedDollKey(oldKey, newKey, payload.Identity);
        if (ownedUpdated)
        {
            lock (_ownerSettingsLock)
            {
                if (_ownerSettingsByDollKey.Remove(oldKey, out var snap))
                {
                    snap = CloneOwnerSettings(snap);
                    // Rebuild under new key — Clone keeps DollKey; fix it.
                    var moved = new OwnerSettingsSnapshot
                    {
                        DollKey = newKey,
                        MaxWindHours = snap.MaxWindHours,
                        AutoGroundSit = snap.AutoGroundSit,
                        LockEmoteId = snap.LockEmoteId,
                        SettingsLocked = snap.SettingsLocked,
                        Emotes = snap.Emotes,
                        LastError = snap.LastError,
                        HasData = snap.HasData,
                    };
                    _ownerSettingsByDollKey[newKey] = moved;
                }
            }
        }

        // Trust only when OldKey matches an existing pair (blocks forged rotations).
        var pair = _config.FindPairByKey(oldKey);
        if (pair is null)
        {
            if (ownedUpdated)
            {
                _config.Save();
                _log.Information("WindUpKey owned doll key rotated {Old} -> {New}", oldKey, newKey);
            }
            else
                _log.Information("WindUpKey ignored keyRotated: oldKey={Old} not paired", oldKey);
            return;
        }

        if (!string.Equals(oldKey, newKey, StringComparison.Ordinal))
        {
            if (_config.FindPairByKey(newKey) is not null
                && !string.Equals(PairingKeyUtil.Normalize(pair.PartnerKey), newKey, StringComparison.Ordinal))
            {
                _log.Warning("WindUpKey keyRotated refused: newKey={New} already paired", newKey);
                return;
            }

            pair.PartnerKey = newKey;
            ClearPresence(oldKey);
        }

        if (!string.IsNullOrWhiteSpace(payload.Identity))
            pair.Identity = PlayerIdentity.Normalize(payload.Identity);

        _config.Save();
        _log.Information("WindUpKey partner key rotated {Old} -> {New}", oldKey, newKey);

        var label = string.IsNullOrWhiteSpace(pair.Identity) ? newKey : pair.Identity;
        PluginChat.Print(_chat, $"Partner key updated for {label}.", PluginChat.Green);
    }

    /// <summary>Updates a partner's pairing key in place (manual rename/transfer fallback).</summary>
    public bool ReplacePartnerKey(string oldKeyRaw, string newKeyRaw)
    {
        var oldKey = PairingKeyUtil.Normalize(oldKeyRaw);
        if (!_config.TryReplacePartnerKey(oldKeyRaw, newKeyRaw))
            return false;

        var newKey = PairingKeyUtil.Normalize(newKeyRaw);
        if (!string.Equals(oldKey, newKey, StringComparison.Ordinal))
            ClearPresence(oldKey);

        _config.Save();
        return true;
    }

    private async Task TrySendPendingKeyRotationAsync(string partnerKey, CancellationToken ct)
    {
        var peer = PairingKeyUtil.Normalize(partnerKey);
        if (!PairingKeyUtil.IsValid(peer) || !IsConnected || !PairingKeyUtil.IsValid(_config.PairingKey))
            return;

        var pending = _config.PendingKeyRotations.FirstOrDefault(r =>
            string.Equals(r.PartnerKey, peer, StringComparison.Ordinal));
        if (pending is null)
            return;

        // Only announce under the new key we registered with.
        if (!string.Equals(pending.NewKey, _config.PairingKey, StringComparison.Ordinal))
            return;

        if (GetPartnerPresence(peer) != PartnerPresence.Online)
            return;

        var payload = new KeyRotatedPayload
        {
            From = _config.PairingKey,
            To = peer,
            OldKey = pending.OldKey,
            NewKey = pending.NewKey,
            Identity = string.IsNullOrWhiteSpace(pending.Identity) ? null : pending.Identity,
        };

        await SendEnvelopeAsync(Envelope.Create(MessageTypes.KeyRotated, payload), ct).ConfigureAwait(false);

        _config.PendingKeyRotations.RemoveAll(r =>
            string.Equals(r.PartnerKey, peer, StringComparison.Ordinal));
        _config.Save();
        _log.Information("WindUpKey sent keyRotated to {Peer} ({Old} -> {New})", peer, pending.OldKey, pending.NewKey);
    }

    private void SilentRemovePartner(string peerKey)
    {
        var removed = _config.PairedPartners.RemoveAll(p =>
            string.Equals(PairingKeyUtil.Normalize(p.PartnerKey), peerKey, StringComparison.Ordinal));
        var ownedRemoved = _config.RemoveOwnedDoll(peerKey);
        if (removed > 0 || ownedRemoved)
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

        ApplyLocalOwnerGrant(dollKey, payload.Identity);
    }

    private void HandleOwnerRevoked(Envelope envelope)
    {
        var payload = envelope.GetPayload<OwnerRevokedPayload>();
        if (payload is null)
            return;

        var dollKey = PairingKeyUtil.Normalize(payload.From);
        if (!PairingKeyUtil.IsValid(dollKey))
            return;

        if (!_config.RemoveOwnedDoll(dollKey))
            return;

        _config.Save();
        ClearOwnerSettings(dollKey);
        PluginChat.Print(_chat, "Your ownership of a doll was cleared.", PluginChat.Grey);
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
            Emotes = emotes,
        }), ct).ConfigureAwait(false);
    }

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

        if (payload.MaxWindHours is { } hours)
            _config.MaxWindHours = Math.Clamp(Math.Round(hours), 1, 168);

        if (payload.AutoGroundSit is { } autoSit)
            _config.AutoGroundSit = autoSit;

        if (payload.LockEmoteId is { } emoteId)
            _config.LockEmoteId = emoteId == 0 ? (ushort)52 : emoteId;

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
            _ownerSettingsByDollKey[dollKey] = new OwnerSettingsSnapshot
            {
                DollKey = dollKey,
                MaxWindHours = payload.MaxWindHours,
                AutoGroundSit = payload.AutoGroundSit,
                LockEmoteId = payload.LockEmoteId == 0 ? (ushort)52 : payload.LockEmoteId,
                SettingsLocked = payload.SettingsLocked,
                Emotes = (payload.Emotes ?? [])
                    .Select(e => (e.Id, e.Name ?? string.Empty))
                    .ToList(),
                LastError = null,
                HasData = true,
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
            existing.LockEmoteId = payload.LockEmoteId == 0 ? (ushort)52 : payload.LockEmoteId;
            existing.SettingsLocked = payload.SettingsLocked;
            existing.LastError = null;
            existing.HasData = true;
        }

        if (notify)
            PluginChat.Print(_chat, "Doll settings updated.", PluginChat.Green);
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
        }
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
            LastError = snap.LastError,
            HasData = snap.HasData,
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

    private async Task SendEnvelopeAsync(Envelope envelope, CancellationToken ct)
    {
        if (_socket is not { State: WebSocketState.Open })
            return;

        var bytes = Encoding.UTF8.GetBytes(envelope.Serialize());
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void CloseSocket()
    {
        try
        {
            _socket?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "WindUpKey CloseSocket");
        }

        _socket = null;
    }

    public void Dispose()
    {
        Stop();
        _sendLock.Dispose();
    }
}
