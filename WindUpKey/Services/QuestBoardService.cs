using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.DutyState;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using WindUpKey.Quest;
using WindUpKey.Sources;

namespace WindUpKey.Services;

/// <summary>
/// The doll's quest board: rolls the daily Easy and weekly Medium/Hard slots, credits synced duty clears and MSQ turn-ins,
/// puts stickers on the 4×4 board and awards wind (up to the overwind cap). The rules live in <see cref="QuestBoardEngine"/>
/// and <see cref="QuestRollRules"/>; this wires them to the game. Framework thread only.
/// </summary>
public sealed class QuestBoardService : IWindUpSource
{
    /// <summary>How often the board checks for resets and MSQ progress.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly Configuration _config;
    private readonly WindTimerService _timer;
    private readonly IDutyState _dutyState;
    private readonly IDataManager _data;
    private readonly IClientState _clientState;
    private readonly ICondition _condition;
    private readonly IFramework _framework;
    private readonly IPartyList _party;
    private readonly IChatGui _chat;
    private readonly IPluginLog _log;
    private readonly QuestGameFacts _facts;
    private readonly Random _rng = new();

    private bool _enabled;

    /// <summary>The duty being watched, from its start until it's completed or left (it survives a disconnect into it).</summary>
    private DutyObservation? _duty;

    /// <summary>The duty just completed is still running (no new start until it ends).</summary>
    private bool _ignoreCurrentDuty;

    private bool _wasDutyStarted;

    /// <summary>The board's periods when the doll entered the current duty's area (null outside duties).</summary>
    private BoardPeriods? _boardAtZoneIn;
    private long _lastPoll;

    /// <summary>The roulette the doll is in (one the Daily slot counts), from entering its duty until leaving it.</summary>
    private RouletteRun? _rouletteRun;

    private bool _wasBoundByDuty;

    public QuestBoardService(
        Configuration config,
        WindTimerService timer,
        IDutyState dutyState,
        IDataManager data,
        IClientState clientState,
        ICondition condition,
        IFramework framework,
        IPlayerState playerState,
        IPartyList party,
        IChatGui chat,
        IPluginLog log)
    {
        _config = config;
        _timer = timer;
        _dutyState = dutyState;
        _data = data;
        _clientState = clientState;
        _condition = condition;
        _framework = framework;
        _party = party;
        _chat = chat;
        _log = log;
        _facts = new QuestGameFacts(data, playerState);
    }

    /// <summary>The last sticker that finished a line, for the board's clear animation (in-memory only).</summary>
    public QuestLineClear? LastLineClear { get; private set; }

    /// <summary>Sends one batch of unpaid lines (lines, requestId) to the relay; set by the plugin.</summary>
    public Action<int, string>? GrantSprings { get; set; }

    /// <summary>How often unpaid lines are sent again while the relay hasn't answered for them.</summary>
    private static readonly TimeSpan GrantResendInterval = TimeSpan.FromSeconds(30);

    private long _lastGrantSend;

    /// <summary>The relay answered a spring grant (framework thread): that batch is paid, or past the day's limit.</summary>
    public void OnSpringGrantSettled(string? requestId)
    {
        if (QuestBoardEngine.SettleSpringGrant(Board, requestId))
            _config.Save();
    }

    /// <summary>
    /// Sends every unpaid batch (each keeps its id, so the relay pays it once however often it's sent), at most every
    /// <see cref="GrantResendInterval"/> unless <paramref name="force"/>.
    /// </summary>
    private void SendSpringGrants(bool force = false)
    {
        var board = Board;
        if (board.PendingSpringGrants.Count == 0 || GrantSprings is null || CanGrantSprings?.Invoke() != true)
            return;
        if (!force && Stopwatch.GetElapsedTime(_lastGrantSend) < GrantResendInterval)
            return;

        _lastGrantSend = Stopwatch.GetTimestamp();
        var changed = false;
        foreach (var grant in board.PendingSpringGrants)
        {
            changed |= !grant.Sent;
            grant.Sent = true;
            GrantSprings(grant.Lines, grant.RequestId);
        }

        if (changed)
            _config.Save();
    }

    /// <summary>
    /// Whether the relay can pay quest springs now: connected, and it has reported a spring balance (a relay with
    /// springs off never does, so nothing is sent to it). Set by the plugin.
    /// </summary>
    public Func<bool>? CanGrantSprings { get; set; }

    /// <summary>A doll's board pays wind as well as springs; a winder's pays springs only.</summary>
    public bool ForDoll => _config.IsDoll;

    /// <summary>The doll is on vacation: the board neither rolls nor awards.</summary>
    public bool OnVacation => _config.IsOnVacation;

    public QuestBoardState Board => _config.QuestBoard ??= new QuestBoardState();

    public void Enable()
    {
        if (_enabled)
            return;

        _dutyState.DutyStarted += OnDutyStarted;
        _dutyState.DutyCompleted += OnDutyCompleted;
        _clientState.Logout += OnLogout;
        _clientState.TerritoryChanged += OnTerritoryChanged;
        _framework.Update += OnFrameworkUpdate;
        _enabled = true;

        if (_dutyState.IsDutyStarted)
            BeginObservedDuty();
    }

    public void Dispose()
    {
        if (!_enabled)
            return;

        _dutyState.DutyStarted -= OnDutyStarted;
        _dutyState.DutyCompleted -= OnDutyCompleted;
        _clientState.Logout -= OnLogout;
        _clientState.TerritoryChanged -= OnTerritoryChanged;
        _framework.Update -= OnFrameworkUpdate;
        _enabled = false;
        _duty = null;
    }

    /// <summary>The board has this reset's slots (it rolls soon after login and after each reset).</summary>
    public bool IsRolled
    {
        get
        {
            // At or after the current periods: a PC clock set back doesn't make the board roll again when it returns.
            var now = DateTimeOffset.UtcNow;
            var board = Board;
            return board.DailyPeriodStartUtc >= QuestResetClock.DailyPeriodStart(now)
                   && board.WeeklyPeriodStartUtc >= QuestResetClock.WeeklyPeriodStart(now)
                   && board.RulesVersion >= QuestBoardState.CurrentRules
                   && board.Slots.Any(s => s.Difficulty == QuestDifficulty.Daily);
        }
    }

    public bool CanReroll(QuestDifficulty difficulty) =>
        _config.HasChosenRole && !_config.IsOnVacation && IsRolled && QuestRollRules.CanReroll(Board, difficulty, DateTimeOffset.UtcNow);

    /// <summary>Rerolls the open Easy (once a day), Medium or Hard (once a week) slots. Returns false when it isn't allowed.</summary>
    public bool TryReroll(QuestDifficulty difficulty)
    {
        if (!CanReroll(difficulty) || !IsActiveCharacterReady())
            return false;

        var duties = QuestContentCatalog.Duties(_data);
        var facts = _facts.Read(duties, hasNextMsq: false);
        if (facts is null || !QuestRollRules.Reroll(Board, difficulty, duties, facts, _rng, DateTimeOffset.UtcNow))
            return false;

        _config.Save();
        _log.Information("Quest board: {Difficulty} rerolled", difficulty);
        return true;
    }

    /// <summary>A duty's name in the client language, for the board.</summary>
    public string DutyName(uint contentFinderConditionId)
    {
        if (_data.GetExcelSheet<ContentFinderCondition>()?.TryGetRow(contentFinderConditionId, out var row) == true)
        {
            var name = row.Name.ExtractText();
            if (!string.IsNullOrWhiteSpace(name))
                return char.ToUpperInvariant(name[0]) + name[1..];
        }

        return $"Duty #{contentFinderConditionId}";
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        // DutyStarted doesn't fire when joining a duty in progress or loading back in after a disconnect, so a duty that
        // starts without it is picked up here (as CommendationTracker does).
        // The board as it was on entering the duty's area, so a reset between zoning in and the duty starting (waiting on
        // the party, or joined in progress before combat) still leaves the slot the doll queued for to credit.
        if (!_condition[ConditionFlag.BoundByDuty])
            _boardAtZoneIn = null;
        else if (_boardAtZoneIn is null)
            _boardAtZoneIn = new BoardPeriods(IsRolled, Board.DailyPeriodStartUtc, Board.WeeklyPeriodStartUtc);

        // A roulette run, from the queue the doll entered by, for the Daily slot. Leaving the duty ends it.
        var bound = _condition[ConditionFlag.BoundByDuty] || _condition[ConditionFlag.BoundByDuty56] || _condition[ConditionFlag.BoundByDuty95];
        if (bound)
            TrackRouletteRun();
        else if (_wasBoundByDuty)
            OnLeftDuty();
        _wasBoundByDuty = bound;

        var dutyStarted = _dutyState.IsDutyStarted;
        if (dutyStarted && !_wasDutyStarted && !_ignoreCurrentDuty)
            BeginObservedDuty();
        if (!dutyStarted)
            _ignoreCurrentDuty = false;
        _wasDutyStarted = dutyStarted;

        if (Stopwatch.GetElapsedTime(_lastPoll) < PollInterval)
            return;
        _lastPoll = Stopwatch.GetTimestamp();

        if (!IsActiveCharacterReady() || _condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51])
            return;

        try
        {
            // A reset during a duty waits for it to end, so the slot the doll queued for is still there to credit.
            SeedMsqHistory();
            // Vacation: no rolls, credits or quest springs. Turn-ins done meanwhile are recorded, so they never pay later.
            if (_config.IsOnVacation)
            {
                ForgetMsqTurnInsOnVacation();
                return;
            }

            SendSpringGrants();
            // Also while bound by duty before it starts (zoned in, waiting on the party; joined in progress before
            // combat): the duty is only watched from its start.
            if (_duty is null && !_condition[ConditionFlag.BoundByDuty])
                EnsureRolled();
            PollMsq();
        }
        catch (Exception ex)
        {
            // Fail open: a game update that moves a read must never break the framework tick.
            _log.Warning(ex, "Quest board tick failed");
        }
    }

    /// <summary>
    /// A logged-in doll or winder whose own profile is the active one. On a character switch the profile changes in
    /// RelayClient.Tick, which can run after this service's tick in the same frame: until then the active board, role
    /// and wind are still the previous character's, and the game reads are already the new one's.
    /// </summary>
    private bool IsActiveCharacterReady() =>
        _clientState.IsLoggedIn
        && _config.HasChosenRole
        && _facts.IsLoaded
        && _facts.ContentId != 0
        && string.Equals(_config.ActiveContentId, Configuration.FormatContentId(_facts.ContentId), StringComparison.Ordinal);

    /// <summary>Once per character: the MSQ done before the board existed never gives wind (New Game+ replays).</summary>
    private void SeedMsqHistory()
    {
        var board = Board;
        if (board.MsqHistorySeeded)
            return;

        QuestBoardEngine.SeedMsqHistory(board, _facts.Completed(QuestContentCatalog.MainScenarioQuestIds(_data)));
        _config.Save();
        _log.Information("Quest board: MSQ history seeded ({Count} quests)", board.CreditedMsqQuestIds.Count);
    }

    /// <summary>
    /// Rolls the Daily slot after a daily reset, and the Easy, Medium and Hard slots after a weekly one. A board from
    /// before 0.2.10.0 (<see cref="QuestBoardState.RulesVersion"/>) rolls its Easy set again at once, under the new rules.
    /// </summary>
    private void EnsureRolled()
    {
        var now = DateTimeOffset.UtcNow;
        var daily = QuestResetClock.DailyPeriodStart(now);
        var weekly = QuestResetClock.WeeklyPeriodStart(now);
        var board = Board;
        var needDaily = board.DailyPeriodStartUtc is not { } rolledDaily || rolledDaily < daily
                        || !board.Slots.Any(s => s.Difficulty == QuestDifficulty.Daily);
        var needWeekly = board.WeeklyPeriodStartUtc is not { } rolledWeekly || rolledWeekly < weekly;
        var needEasy = needWeekly || board.RulesVersion < QuestBoardState.CurrentRules;
        if (!needDaily && !needWeekly && !needEasy)
            return;

        // The Daily slot needs nothing from the character; the rest do.
        if (needDaily)
            QuestRollRules.ReplaceDaily(board, QuestRollRules.RollDaily(), daily);

        if (needEasy || needWeekly)
        {
            var duties = QuestContentCatalog.Duties(_data);
            if (_facts.Read(duties, hasNextMsq: false) is { } facts)
            {
                if (needEasy)
                    QuestRollRules.ReplaceEasy(board, QuestRollRules.RollEasy(duties, facts, _rng));
                if (needWeekly)
                    QuestRollRules.ReplaceWeekly(board, QuestRollRules.RollWeekly(duties, facts, _rng), weekly);
            }
            else if (!needDaily)
            {
                return;
            }
        }

        _config.Save();
        _log.Information(
            "Quest board rolled (daily={Daily}, easy={Easy}, weekly={Weekly}, slots={Slots})",
            needDaily,
            needEasy,
            needWeekly,
            board.Slots.Count);
    }

    /// <summary>
    /// Watches the next MSQ: when the one the board waits on is turned in, it gives its wind (once ever per quest; no
    /// sticker or spring since 0.2.10.0). The board then waits on the new next MSQ.
    /// </summary>
    private void PollMsq()
    {
        // A reset waiting to be rolled: keep watching the same quest, so its turn-in counts toward the new day.
        if (!IsRolled)
            return;

        var next = _facts.NextMsqQuestId(out var ready);
        if (!ready)
            return;

        var board = Board;
        var tracked = board.MsqTrackedQuestId;
        if (tracked == next)
            return;

        board.MsqTrackedQuestId = next;
        // Every main scenario quest done since, not only the tracked one: the guide shows one of several (e.g. the Grand
        // Company paths), and two turn-ins can fall between polls.
        var done = _facts.Completed(QuestContentCatalog.MainScenarioQuestIds(_data)).ToList();
        var credits = QuestBoardEngine.CreditMsqTurnIns(board, done, tracked);
        // Turn-ins give wind only, so a winder's board has nothing to give for them (they're still recorded above).
        if (credits.Count > 0 && _config.IsDoll)
        {
            _log.Information("Quest board: {Count} MSQ turn-in(s) credited", credits.Count);
            Award(credits, "Main scenario progress", msq: true);
            return;
        }

        _config.Save();
    }

    /// <summary>
    /// On vacation: main scenario quests turned in are recorded as credited without wind or a sticker, and the board waits
    /// on the new next one, so coming back never pays for them.
    /// </summary>
    private void ForgetMsqTurnInsOnVacation()
    {
        var next = _facts.NextMsqQuestId(out var ready);
        var board = Board;
        if (!ready || board.MsqTrackedQuestId == next)
            return;

        board.MsqTrackedQuestId = next;
        var done = _facts.Completed(QuestContentCatalog.MainScenarioQuestIds(_data));
        var known = board.CreditedMsqQuestIds.ToHashSet();
        board.CreditedMsqQuestIds.AddRange(done.Where(id => id != 0 && known.Add(id)));
        _config.Save();
    }

    private void OnDutyStarted(IDutyStateEventArgs args) => BeginObservedDuty();

    /// <summary>
    /// Starts watching the current duty. The one already watched is kept as it was (a wipe and recommence, a disconnect
    /// and reconnect, or the event and the join-in-progress check both seeing the same start), so its sync, roulette and
    /// start time are the first ones seen.
    /// </summary>
    private void BeginObservedDuty()
    {
        var cfcId = _dutyState.ContentFinderCondition.RowId;
        var contentId = _facts.ContentId;
        if (_duty is { } watched && watched.ContentFinderConditionId == cfcId && watched.ContentId == contentId)
            return;

        _ignoreCurrentDuty = false;
        // Fail open: if a game update breaks the reads, clears still count rather than every doll losing them.
        var synced = true;
        var levelingBelowCap = false;
        var firstClear = false;
        uint territory = 0;
        try
        {
            // Read now: the game marks the duty completed at the clear, which would hide a first clear.
            firstClear = _facts.IsUncleared(cfcId);
            var queue = _facts.ReadQueue();
            if (_data.GetExcelSheet<ContentFinderCondition>()?.TryGetRow(cfcId, out var cfc) == true)
            {
                territory = cfc.TerritoryType.RowId;
                synced = _facts.IsSyncedFor(cfc, queue.UnrestrictedWithoutLevelSync);
            }

            levelingBelowCap = QuestContentCatalog.IsLevelingRoulette(_data, queue.RouletteId) && _facts.IsCurrentJobBelowCap();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Quest board: could not read the duty's sync state");
        }

        var periods = _boardAtZoneIn ?? new BoardPeriods(IsRolled, Board.DailyPeriodStartUtc, Board.WeeklyPeriodStartUtc);
        _duty = new DutyObservation(
            cfcId, territory, contentId, synced, levelingBelowCap, periods.Rolled, periods.Daily, periods.Weekly, firstClear);
    }

    /// <summary>
    /// Leaving the duty's area ends it, completed or not. A wipe stays in the area, and a reconnect comes back to it, so
    /// those keep the watched duty.
    /// </summary>
    private void OnTerritoryChanged(uint territoryId)
    {
        if (_duty is { } watched && watched.TerritoryId != 0 && watched.TerritoryId == territoryId)
            return;

        _duty = null;
        _ignoreCurrentDuty = false;
    }

    private void OnDutyCompleted(IDutyStateEventArgs args)
    {
        if (_ignoreCurrentDuty)
            return;

        // Its start was never seen: joined in progress at the last boss without entering combat, or the plugin (re)loaded
        // in the duty before it was watched. The queue still says how it was entered, so it's read now.
        if (_duty is null && _dutyState.ContentFinderCondition.RowId != 0)
            BeginObservedDuty();
        TrackRouletteRun();
        var watched = _duty;
        _duty = null;
        _ignoreCurrentDuty = _dutyState.IsDutyStarted;
        try
        {
            if (!IsActiveCharacterReady())
                return;
            var duty = watched is { ContentFinderConditionId: not 0 } w && w.ContentId == _facts.ContentId ? w : null;
            var roulette = TakeRoulette();
            if (duty is not null || roulette)
                CreditClear(duty, roulette);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Quest board: crediting the clear failed");
        }
    }

    /// <summary>
    /// While bound by duty: notes a roulette the Daily slot counts (from the queue the doll entered by), once per run. It
    /// counts when the game reports the duty completed (PvP too, by the owner's choice: a match the game never reports
    /// completed doesn't count).
    /// </summary>
    private void TrackRouletteRun()
    {
        if (_rouletteRun is not null)
            return;

        try
        {
            var queue = _facts.ReadQueue();
            if (queue.RouletteId == 0 || !QuestContentCatalog.CountsForDaily(_data, queue.RouletteId))
                return;
            _rouletteRun = new RouletteRun(queue.RouletteId, _facts.ContentId);
        }
        catch (Exception ex)
        {
            _log.Debug("Quest board: roulette read failed: {Type}", ex.GetType().Name);
        }
    }

    /// <summary>The current run's roulette counts now (once per run, for the character it began on).</summary>
    private bool TakeRoulette()
    {
        if (_rouletteRun is not { Credited: false } run || run.ContentId != _facts.ContentId)
            return false;

        _rouletteRun = run with { Credited = true };
        return true;
    }

    /// <summary>
    /// The doll left the duty: the roulette run ends. It counted only if the game reported the duty completed (a PvP match
    /// too: one the game never reports completed simply doesn't count).
    /// </summary>
    private void OnLeftDuty() => _rouletteRun = null;

    /// <summary>
    /// Credits a duty clear (<paramref name="watched"/>, when one was watched) and a roulette that counts for the Daily
    /// slot, as one award.
    /// </summary>
    private void CreditClear(DutyObservation? watched, bool roulette)
    {
        // Vacation: the board doesn't award (the board's periods catch up once the doll is back).
        if (_config.IsOnVacation)
            return;

        // A reset that passed during the duty waited (see OnFrameworkUpdate): the slots the board had when it began still
        // count, and the tick rolls the new ones once it ends. Any other slots are brought up to date first.
        var board = Board;
        var heldOver = watched is not null
                       && watched.BoardRolledAtStart
                       && board.DailyPeriodStartUtc == watched.DailyPeriodAtStart
                       && board.WeeklyPeriodStartUtc == watched.WeeklyPeriodAtStart;
        if (!heldOver)
        {
            EnsureRolled();
            if (!IsRolled)
            {
                // The duty's slots wait on a board that can't be brought up to date (the character couldn't be read);
                // the Daily needs nothing from the character, so a roulette still counts.
                if (!roulette)
                    return;
                watched = null;
            }
        }

        var credits = new List<QuestSlotCredit>();
        var unsyncedEasy = false;
        var firstClear = false;
        var what = "Roulette";
        if (watched is not null)
        {
            var clear = new QuestDutyClear(watched.ContentFinderConditionId, watched.Synced, watched.LevelingRouletteBelowCap, watched.FirstClear);
            var credit = QuestBoardEngine.CreditDuty(board, clear);
            credits.AddRange(credit.Credits);
            unsyncedEasy = credit.UnsyncedEasyMatch;
            firstClear = clear.FirstClear && credit.Credits.Any(c => c.Slot is { } slot && QuestRewardRules.CanBeFirstClear(slot.Kind));
            what = DutyName(clear.ContentFinderConditionId);
            if (credit.Credits.Count > 0)
            {
                _log.Information(
                    "Quest board: clear credited (cfc={Cfc}, synced={Synced}, slots={Slots})",
                    clear.ContentFinderConditionId,
                    clear.Synced,
                    credit.Credits.Count);
            }
        }

        var dailyProgress = false;
        if (roulette)
        {
            // A roulette counts toward the Daily of the day it ends, even when the duty's other slots were held over from
            // before a reset (the Daily slot needs nothing from the character to roll).
            var today = QuestResetClock.DailyPeriodStart(DateTimeOffset.UtcNow);
            if (board.DailyPeriodStartUtc is not { } dailyPeriod || dailyPeriod < today)
                QuestRollRules.ReplaceDaily(board, QuestRollRules.RollDaily(), today);
            if (QuestBoardEngine.CreditRoulette(board) is { } daily)
                credits.Add(daily);
            else
                dailyProgress = board.SlotsOf(QuestDifficulty.Daily).Any(s => !s.Completed && s.Progress > 0);
            _log.Information("Quest board: roulette counted for the Daily slot");
        }

        if (credits.Count == 0)
        {
            // 0.2.10.0: the rules behind a clear that doesn't count are for players to find out.
            if (unsyncedEasy)
                PluginChat.Print(_chat, "Quest board: that clear didn't count this time.", PluginChat.Orange);
            if (dailyProgress && board.SlotsOf(QuestDifficulty.Daily).FirstOrDefault() is { } open)
                PluginChat.Print(_chat, $"Quest board: a roulette toward today's Daily ({open.Progress}/{open.Target}).", PluginChat.Green);
            if (roulette)
                _config.Save();
            return;
        }

        Award(credits, what, firstClear);
        // The same clear also moved the Daily on without finishing it.
        if (dailyProgress && board.SlotsOf(QuestDifficulty.Daily).FirstOrDefault() is { } progressed)
            PluginChat.Print(_chat, $"Quest board: a roulette toward today's Daily ({progressed.Progress}/{progressed.Target}).", PluginChat.Green);
    }

    /// <summary>
    /// The best party bonus now: one of the doll's owners in the party, else any paired partner. Members are matched to
    /// pairs by pairing key (from their ContentId), as Call does; neither is logged. Winders get no wind, so none.
    /// </summary>
    private QuestPartyBonus ReadPartyBonus()
    {
        if (!_config.IsDoll)
            return QuestPartyBonus.None;

        var bonus = QuestPartyBonus.None;
        try
        {
            foreach (var member in _party)
            {
                if (member.ContentId == 0)
                    continue;
                var key = PairingKeyUtil.FromContentId((ulong)member.ContentId);
                if (string.Equals(key, _config.PairingKey, StringComparison.Ordinal) || _config.FindPairByKey(key) is not { } pair)
                    continue;
                if (pair.IsOwner)
                    return QuestPartyBonus.Owner;
                bonus = QuestPartyBonus.Partner;
            }
        }
        catch (Exception ex)
        {
            _log.Debug("Quest board: party read failed: {Type}", ex.GetType().Name);
        }

        return bonus;
    }

    private void Award(IReadOnlyList<QuestSlotCredit> credits, string what, bool firstClear = false, bool msq = false)
    {
        var stickers = credits.Sum(c => c.Reward.Stickers);
        var party = ReadPartyBonus();
        // The party bonus multiplies the slots' own wind; line bonuses stay as they are.
        var hours = credits.Sum(c => c.Reward.Hours) * QuestRewardRules.PartyMultiplier(party);
        var ultimate = credits.Any(c => c.Slot?.Kind == QuestSlotKind.Ultimate);
        var board = Board;
        var award = QuestBoardEngine.PlaceStickers(board, stickers, hours, n => _rng.Next(n));

        if (award.TripleLine)
            _config.Achievements.TryAdd(AchievementFlags.QuestTripleLine, DateTimeOffset.UtcNow);
        var cleared = award.Placements.LastOrDefault(p => p.Lines > 0);
        if (cleared.Lines > 0)
            LastLineClear = new QuestLineClear(cleared.BoardBefore, cleared.LineCells, Stopwatch.GetTimestamp());
        // Each line and each completed slot is paid a spare spring by the relay (it holds the balance), sent from
        // SendSpringGrants until it answers.
        var springUnits = award.LinesCleared + QuestBoardEngine.CompletionSprings(credits);
        if (springUnits > 0)
            QuestBoardEngine.AddSpringLines(board, springUnits, () => Guid.NewGuid().ToString("N"));

        // The board is saved before the wind (AddQuestWind saves too), so a failed wind never re-awards stickers.
        _config.Save();

        // Winders have no wind: their board pays in springs only (double a doll's, which the relay applies).
        if (_config.IsDoll)
        {
            var windHours = award.FillToCap ? QuestRewardRules.OverwindCapHours(_config.MaxWindHours) : award.Hours;
            if (windHours > 0)
                _timer.AddQuestWind(windHours);
        }

        if (springUnits > 0)
            SendSpringGrants(force: true);

        var doll = _config.IsDoll;
        PluginChat.Print(_chat, msq && award.LinesCleared == 0 ? $"Quest board: {what}, and the key turns." : Message(what, stickers, award, doll), PluginChat.Green);
        // 0.2.10.0: a hidden bonus is only hinted at, never explained.
        if (doll && (firstClear || ultimate || party != QuestPartyBonus.None))
            PluginChat.Print(_chat, "Quest board: the key turns more than usual.", PluginChat.Green);
        _log.Information(
            "Quest board award: stickers={Stickers}, lines={Lines}, springs={Springs}, fill={Fill}, first={First}, party={Party}",
            stickers,
            award.LinesCleared,
            springUnits,
            award.FillToCap,
            firstClear,
            party);
    }

    /// <summary>The chat line for an award. 06: it never names hours or how much wind the doll has.</summary>
    private static string Message(string what, int stickers, QuestAward award, bool doll)
    {
        if (!doll)
        {
            if (award.LinesCleared > 0)
                return $"Quest board: {what}. {(award.LinesCleared == 1 ? "A line" : $"{award.LinesCleared} lines at once")}! The board clears.";
            return stickers switch
            {
                0 => $"Quest board: {what}. No sticker this time.",
                1 => $"Quest board: {what}. A sticker for your board.",
                _ => $"Quest board: {what}. {stickers} stickers for your board.",
            };
        }

        if (award.TripleLine)
            return $"Quest board: {what}. Three lines at once! The key turns and turns and turns.";
        if (award.LinesCleared >= 2)
            return $"Quest board: {what}. Two lines at once! The board clears and the key turns with fresh wind.";
        if (award.LinesCleared == 1)
            return $"Quest board: {what}. A line! The board clears and the key turns with fresh wind.";
        if (stickers == 0)
            return $"Quest board: {what}. No sticker this time, but the key turns a little.";
        return stickers == 1
            ? $"Quest board: {what}. A sticker for your board, and the key turns."
            : $"Quest board: {what}. {stickers} stickers for your board, and the key turns.";
    }

    /// <summary>
    /// A logout keeps the watched duty, so a reconnect into it resumes it. Logging in anywhere else is a territory change
    /// that drops it, and another character never resumes it (its ContentId differs).
    /// </summary>
    private void OnLogout(int type, int code)
    {
        _ignoreCurrentDuty = false;
        // The run is dropped (a reconnect into the duty notes it again).
        _rouletteRun = null;
    }
}

/// <summary>A sticker that finished a line: the board just before it cleared, the line cells, and when.</summary>
public sealed record QuestLineClear(ushort BoardBefore, ushort LineCells, long Timestamp);

/// <summary>
/// A duty being watched, as first seen: its synced and Leveling Roulette state, the board's periods at its start (so a
/// reset during it doesn't take its slot away), and whether the character had never cleared it then.
/// </summary>
internal sealed record DutyObservation(
    uint ContentFinderConditionId,
    uint TerritoryId,
    ulong ContentId,
    bool Synced,
    bool LevelingRouletteBelowCap,
    bool BoardRolledAtStart,
    DateTimeOffset? DailyPeriodAtStart,
    DateTimeOffset? WeeklyPeriodAtStart,
    bool FirstClear);

/// <summary>The board's reset periods at one moment, and whether it was rolled for the periods current then.</summary>
internal readonly record struct BoardPeriods(bool Rolled, DateTimeOffset? Daily, DateTimeOffset? Weekly);

/// <summary>A roulette the doll entered a duty by (one the Daily slot counts), and whether it has counted yet.</summary>
internal sealed record RouletteRun(uint RouletteId, ulong ContentId, bool Credited = false);
