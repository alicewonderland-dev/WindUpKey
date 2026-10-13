#if WINDUP_TESTING
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using Lumina.Excel.Sheets;
using WindUpKey.Protocol;
using WindUpKey.Services;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace WindUpKey.SelfTest;

/// <summary>
/// The self-test's thin Dalamud adapter (04 Architecture): every game, config and identity-store read happens in a
/// <see cref="IFramework.RunOnTick{T}"/> callback on the framework thread, once per call, and the live-session link goes
/// through <see cref="RelayClient"/>'s self-test hooks. Signature scans and sheet reads happen once per run, never per
/// frame: the scans on the framework thread, as nothing shows Dalamud's scanner cache is thread-safe (04-D11), and the
/// sheet reads off it (04-D10). Snapshots never take the identity-file lock; the import check that does runs once per
/// run (04-D9). Testing builds only (04-D1).
/// </summary>
internal sealed class SelfTestHost(
    RelayClient relay,
    Configuration config,
    IFramework framework,
    IObjectTable objectTable,
    IDataManager data,
    ISigScanner sigScanner,
    IDalamudPluginInterface pluginInterface,
    InputDiagnosticLog diagnostics,
    Func<bool?> travelReady,
    Action<int> expectEpoch,
    Func<bool> reconnectAllowed) : ISelfTestHost, ISelfTestLink
{
    /// <summary>The folder Dalamud's reset deletes, asked of Dalamud (04-D10): the identity file must be outside it.</summary>
    private readonly string _pluginConfigDirectory = pluginInterface.GetPluginConfigDirectory();

    public ISelfTestLink Link => this;

    /// <summary>04-D10: false once the plugin is unloading or the character logged out (the controller's state).</summary>
    public bool ReconnectAllowed => reconnectAllowed();

    public Task<SessionSnapshot> SnapshotAsync(CancellationToken ct) =>
        framework.RunOnTick(Snapshot, cancellationToken: ct);

    private SessionSnapshot Snapshot()
    {
        var now = DateTimeOffset.UtcNow;
        var derived = PairingKeyUtil.FromContentIdHex(config.ActiveContentId);
        var futureLimit = now + SyncStamp.MaxFutureSkew;
        var futureStamps = config.PairedPartners.Count(p => p?.ConsentUpdatedUtc is { } stamp && stamp > futureLimit)
                           + (config.WindUpdatedUtc is { } wind && wind > futureLimit ? 1 : 0);
        return new SessionSnapshot
        {
            OwnKey = config.PairingKey,
            PairingKeyMatchesContentId = PairingKeyUtil.IsValid(derived) && string.Equals(derived, config.PairingKey, StringComparison.Ordinal),
            RegisteredKey = relay.RegisteredKey,
            ContentIdKey = PairingKeyUtil.IsValid(derived) ? derived : null,
            TokenMatchesCompiled = string.Equals(config.RelayToken, RelayDefaults.RelayToken, StringComparison.Ordinal),
            PartnerKeys = config.PairedPartners.Where(p => p is not null).Select(p => p.PartnerKey).ToList(),
            PendingKeys = config.PendingPartnerKeys.ToList(),
            PendingRemovalKeys = config.PendingPairRemovals.Where(r => r is not null).Select(r => r.PeerKey).ToList(),
            OwnedDollKeys = config.OwnedDolls.Where(d => d is not null).Select(d => d.DollKey).ToList(),
            RelayPeerKeys = relay.RegisterAccepted ? relay.LastPeerKeys : null,
            Connected = relay.IsConnected,
            ConnectedUrl = relay.ConnectedRelayUrl,
            RegisterAccepted = relay.RegisterAccepted,
            Invariants = new SessionInvariants(relay.AcceptedConnections, relay.IsConnected, relay.AuthBlockStatus is not null, relay.SessionReplacedSeen),
            AuthBlockCode = relay.AuthBlockCode,
            LastSuccessfulRelayUrl = config.LastSuccessfulRelayUrl,
            CompiledUrls = RelayDefaults.RelayUrls,
            ShouldSuspendMovementLocks = relay.ShouldSuspendMovementLocks,
            FutureStampCount = futureStamps,
            // The store's current view only: no lock, no read, no write (04-D10). 01-4's copy and import parts come from
            // IdentityCopyCheckAsync, once per run.
            Identity = relay.DiagnoseRelayIdentity(_pluginConfigDirectory),
        };
    }

    /// <summary>
    /// 01-4 through the real paths (the Copy button's secret and <see cref="RelayClient.CheckRelaySecretImport"/>), which
    /// re-read the identity file under its lock: called once per run (04-D9), never from a snapshot.
    /// </summary>
    public Task<IdentityCopyCheck> IdentityCopyCheckAsync(CancellationToken ct) =>
        framework.RunOnTick(() => new IdentityCopyCheck(
            RelaySecret.IsValid(relay.ActiveRelaySecret),
            relay.CheckCurrentRelayIdentityImport()), cancellationToken: ct);

    /// <summary>
    /// 04-D3: every value the report's scrubber replaces: this character's secret, previous secret and their hashes, the
    /// configured and compiled relay token, the ContentId, and every full key it knows. For the scrubber only.
    /// </summary>
    public Task<IReadOnlyList<string>> SensitiveValuesAsync(CancellationToken ct) =>
        framework.RunOnTick<IReadOnlyList<string>>(() =>
        {
            var values = new List<string>(relay.SelfTestSensitiveValues())
            {
                config.RelayToken,
                RelayDefaults.RelayToken,
                config.ActiveContentId,
                config.PairingKey,
            };
            values.AddRange(config.PairedPartners.Where(p => p is not null).Select(p => p.PartnerKey));
            values.AddRange(config.PendingPartnerKeys);
            values.AddRange(config.PendingPairRemovals.Where(r => r is not null).Select(r => r.PeerKey));
            values.AddRange(config.OwnedDolls.Where(d => d is not null).Select(d => d.DollKey));
            values.AddRange(relay.LastPeerKeys ?? []);
            return values;
        }, cancellationToken: ct);

    public Task<ProbeSecret?> FreshRandomSecretAsync(CancellationToken ct) =>
        framework.RunOnTick(() => ProbeSecret.FreshRandom(relay.IsKnownRelaySecret), cancellationToken: ct);

    public void SetTap(SelfTestTap? tap) => relay.SetSelfTestTap(tap);

    public Task<RelayLiveState> StateAsync(CancellationToken ct) => framework.RunOnTick(LiveState, cancellationToken: ct);

    /// <summary>Framework thread.</summary>
    private RelayLiveState LiveState() => new(
        relay.IsConnected,
        relay.RegisterAccepted,
        relay.AcceptedConnections,
        relay.ShouldSuspendMovementLocks,
        relay.AuthBlockStatus is not null,
        relay.SessionReplacedSeen,
        relay.CloseCount,
        relay.LastCloseStatus,
        relay.LastCloseDescription);

    public Task SetOutageHoldAsync(bool hold, CancellationToken ct) =>
        framework.RunOnTick(() => relay.SetSelfTestOutageHold(hold), cancellationToken: ct);

    /// <summary>
    /// The Reconnect button's path (ConfigWindow): compiled defaults, then Start(). The run expects the new epoch.
    /// Re-checks <see cref="ReconnectAllowed"/> on the framework thread, since a tick queued before an unload can still
    /// run after it (04-D10); <see cref="RelayClient.Start"/> is also a no-op once disposed. Re-checks the takeover risk
    /// there too (04-D11): session_replaced and auth blocks are applied on the framework thread, so none can land between
    /// this check and Start(). The outage's hold is lifted in this same tick (04-D12): Start() replaces the connect loop
    /// before it can reconnect on its own.
    /// </summary>
    public Task ReconnectAsync(RelayLiveState before, bool liftOutageHold, CancellationToken ct) =>
        framework.RunOnTick(() =>
        {
            if (liftOutageHold)
                relay.SetSelfTestOutageHold(false);
            if (!reconnectAllowed() || SelfTestSuite.TakeoverRisk(before, LiveState()) is not null)
                return;
            config.ApplyRelayDefaults();
            relay.Start();
            expectEpoch(relay.ConnectEpoch);
        }, cancellationToken: ct);

    public async Task<bool> SendAsync(Envelope envelope, CancellationToken ct)
    {
        try
        {
            return await relay.SendSelfTestAsync(envelope, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException
                                       || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return false;
        }
    }

    public async Task<bool> SendRawAsync(byte[] utf8, CancellationToken ct)
    {
        try
        {
            return await relay.SendSelfTestRawAsync(utf8, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException
                                       || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return false;
        }
    }

    // ---------- 04-D8 post-patch facts ----------

    /// <summary>
    /// Once per run. On the framework thread: the local player's world, installed plugins, travel readiness and the
    /// signature scans (04-D11). Then the sheet reads on the thread pool (04-D10).
    /// </summary>
    public async Task<PostPatchFacts> PostPatchFactsAsync(CancellationToken ct)
    {
        var (worldId, ipc, ready, signatures) = await framework.RunOnTick(
            () => (objectTable.LocalPlayer?.CurrentWorld.RowId,
                   (IReadOnlyList<IpcFact>)[Ipc("P-ipc-lifestream", "Lifestream"), Ipc("P-ipc-vnavmesh", "vnavmesh")],
                   travelReady(),
                   ScanSignatures()),
            cancellationToken: ct).ConfigureAwait(false);
        return await Task.Run(() => CollectPostPatch(worldId, ipc, ready, signatures), ct).ConfigureAwait(false);
    }

    /// <summary>Framework thread (04-D11): resolves the hook signatures without installing a hook.</summary>
    private IReadOnlyList<SignatureFact> ScanSignatures() =>
    [
        new SignatureFact("P-hook-rmi-walk", "RMI walk", sigScanner.TryScanText(LockController.RmiWalkSignature, out var walk) && walk != 0),
        new SignatureFact("P-hook-rmi-fly", "RMI fly", sigScanner.TryScanText(LockController.RmiFlySignature, out var fly) && fly != 0),
    ];

    /// <summary>Thread pool: Lumina sheets, ClientStructs addresses and the recorded hook results.</summary>
    private unsafe PostPatchFacts CollectPostPatch(uint? localWorldId, IReadOnlyList<IpcFact> ipc, bool? ready, IReadOnlyList<SignatureFact> signatures)
    {
        var sheets = new List<SheetFact>();
        var rows = new List<SheetRowFact>();
        int? loopingEmotes = null;

        var emotes = data.GetExcelSheet<Emote>();
        sheets.Add(new SheetFact("P-data-emote", "Emote", emotes is not null));
        if (emotes is not null)
        {
            rows.Add(new SheetRowFact("P-data-emote", "Emote", GameCommandRunner.SitEmoteId, "Sit", emotes.TryGetRow(GameCommandRunner.SitEmoteId, out _)));
            rows.Add(new SheetRowFact("P-data-emote", "Emote", CharacterProfile.GroundSitEmoteId, "Sit on Ground", emotes.TryGetRow(CharacterProfile.GroundSitEmoteId, out _)));
            // The looping-emote query GameCommandRunner uses (EmoteMode and TextCommand set), over the whole sheet.
            loopingEmotes = emotes.Count(e => e.EmoteMode.RowId != 0 && e.TextCommand.RowId != 0);
        }

        var territories = data.GetExcelSheet<TerritoryType>();
        sheets.Add(new SheetFact("P-data-territory", "TerritoryType", territories is not null));
        if (territories is not null)
        {
            foreach (var territory in HousingCallLocation.ResidentialTerritoryIds)
            {
                var present = territories.TryGetRow(territory, out var row);
                var city = HousingCallLocation.TryGetCityForTerritory(territory);
                var met = present && city is not null && HousingCallLocation.TryGetCityByPlaceNameRegion(row.PlaceNameRegion.RowId) == city;
                rows.Add(new SheetRowFact("P-data-territory", "TerritoryType", territory, "residential district", present,
                    "its PlaceNameRegion maps to the same city", met));
            }
        }

        var worlds = data.GetExcelSheet<World>();
        sheets.Add(new SheetFact("P-data-world", "World", worlds is not null));
        if (worlds is not null && localWorldId is { } worldId)
        {
            var present = worlds.TryGetRow(worldId, out var world);
            rows.Add(new SheetRowFact("P-data-world", "World", worldId, "this world", present, "a data center is set", present && world.DataCenter.RowId != 0));
        }

        var aetherytes = data.GetExcelSheet<Aetheryte>();
        sheets.Add(new SheetFact("P-data-aetheryte", "Aetheryte", aetherytes is not null));
        if (aetherytes is not null)
        {
            // Each residential district's city aetheryte, as HousingCallLocation maps them.
            foreach (var city in HousingCallLocation.ResidentialTerritoryIds.Select(HousingCallLocation.TryGetCityForTerritory).OfType<int>())
            {
                var present = aetherytes.TryGetRow((uint)city, out var row);
                rows.Add(new SheetRowFact("P-data-aetheryte", "Aetheryte", (uint)city, "residential aetheryte", present, "it is an aetheryte", present && row.IsAetheryte));
            }
        }

        var generalActions = data.GetExcelSheet<GeneralAction>();
        sheets.Add(new SheetFact("P-data-general-action", "GeneralAction", generalActions is not null));
        if (generalActions is not null)
        {
            foreach (var (id, label) in new[]
                     {
                         (LockController.GeneralActionJump, "Jump"),
                         (LockController.GeneralActionReturn, "Return"),
                         (LockController.GeneralActionTeleport, "Teleport"),
                         (CallTravelService.GeneralActionMountRoulette, "Mount Roulette"),
                     })
                rows.Add(new SheetRowFact("P-data-general-action", "GeneralAction", id, label, generalActions.TryGetRow(id, out _)));
        }

        var actions = data.GetExcelSheet<LuminaAction>();
        sheets.Add(new SheetFact("P-data-action", "Action", actions is not null));
        if (actions is not null)
            rows.Add(new SheetRowFact("P-data-action", "Action", 5, "Teleport", actions.TryGetRow(5, out _)));

        return new PostPatchFacts
        {
            Signatures = signatures,
            Addresses =
            [
                new AddressFact("P-addr-use-action", "ActionManager.UseAction", (nint)ActionManager.MemberFunctionPointers.UseAction != 0),
                new AddressFact("P-addr-set-rotation", "GameObject.SetRotation", (nint)CSGameObject.MemberFunctionPointers.SetRotation != 0),
                new AddressFact("P-addr-input-pressed", "InputData.IsInputIdPressed", (nint)InputData.MemberFunctionPointers.IsInputIdPressed != 0),
                new AddressFact("P-addr-input-down", "InputData.IsInputIdDown", (nint)InputData.MemberFunctionPointers.IsInputIdDown != 0),
                new AddressFact("P-addr-input-held", "InputData.IsInputIdHeld", (nint)InputData.MemberFunctionPointers.IsInputIdHeld != 0),
            ],
            LastHookResults = diagnostics.SnapshotHookResults().Select(h => new HookResultFact(h.Name, h.Installed, h.AtUtc)).ToList(),
            Sheets = sheets,
            Rows = rows,
            LoopingEmoteRows = loopingEmotes,
            Ipc = ipc,
            TravelReady = ready,
        };
    }

    /// <summary>Framework thread.</summary>
    private IpcFact Ipc(string id, string internalName)
    {
        var plugin = pluginInterface.InstalledPlugins.FirstOrDefault(p => string.Equals(p.InternalName, internalName, StringComparison.OrdinalIgnoreCase));
        return new IpcFact(id, internalName, plugin is not null, plugin?.IsLoaded == true);
    }
}
#endif
