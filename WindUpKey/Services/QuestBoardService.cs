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

    /// <summary>How long a daily roll waits for the scenario guide after login before rolling without an MSQ slot.</summary>
    private static readonly TimeSpan MsqReadyWait = TimeSpan.FromSeconds(60);

    private readonly Configuration _config;
    private readonly WindTimerService _timer;
    private readonly IDutyState _dutyState;
    private readonly IDataManager _data;
    private readonly IClientState _clientState;
    private readonly ICondition _condition;
    private readonly IFramework _framework;
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
    private long _loggedInAt = Stopwatch.GetTimestamp();

    public QuestBoardService(
        Configuration config,
        WindTimerService timer,
        IDutyState dutyState,
        IDataManager data,
        IClientState clientState,
        ICondition condition,
        IFramework framework,
        IPlayerState playerState,
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
        _chat = chat;
        _log = log;
        _facts = new QuestGameFacts(data, playerState);
    }

    /// <summary>The last sticker that finished a line, for the board's clear animation (in-memory only).</summary>
    public QuestLineClear? LastLineClear { get; private set; }

    public QuestBoardState Board => _config.QuestBoard ??= new QuestBoardState();

    public void Enable()
    {
        if (_enabled)
            return;

        _dutyState.DutyStarted += OnDutyStarted;
        _dutyState.DutyCompleted += OnDutyCompleted;
        _clientState.Login += OnLogin;
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
        _clientState.Login -= OnLogin;
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
            return Board.DailyPeriodStartUtc >= QuestResetClock.DailyPeriodStart(now)
                   && Board.WeeklyPeriodStartUtc >= QuestResetClock.WeeklyPeriodStart(now);
        }
    }

    public bool CanReroll(QuestDifficulty difficulty) => _config.IsDoll && IsRolled && QuestRollRules.CanReroll(Board, difficulty);

    /// <summary>Rerolls the open Medium or Hard slots (once a week each). Returns false when it isn't allowed.</summary>
    public bool TryReroll(QuestDifficulty difficulty)
    {
        if (!CanReroll(difficulty) || !IsActiveCharacterReady())
            return false;

        var duties = QuestContentCatalog.Duties(_data);
        var facts = _facts.Read(duties, hasNextMsq: false);
        if (facts is null || !QuestRollRules.Reroll(Board, difficulty, duties, facts, _rng))
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

    /// <summary>The name of the MSQ the board is waiting on, in the client language, or null.</summary>
    public string? TrackedMsqName()
    {
        var id = Board.MsqTrackedQuestId;
        if (id == 0 || _data.GetExcelSheet<Lumina.Excel.Sheets.Quest>()?.TryGetRow(id, out var quest) != true)
            return null;

        var name = quest.Name.ExtractText();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private void OnLogin() => _loggedInAt = Stopwatch.GetTimestamp();

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
    /// A logged-in doll whose own profile is the active one. On a character switch the profile changes in
    /// RelayClient.Tick, which can run after this service's tick in the same frame: until then the active board, role
    /// and wind are still the previous character's, and the game reads are already the new one's.
    /// </summary>
    private bool IsActiveCharacterReady() =>
        _clientState.IsLoggedIn
        && _config.IsDoll
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

    /// <summary>Rolls the Easy slots after a daily reset and the Medium/Hard slots after a weekly one.</summary>
    private void EnsureRolled()
    {
        var now = DateTimeOffset.UtcNow;
        var daily = QuestResetClock.DailyPeriodStart(now);
        var weekly = QuestResetClock.WeeklyPeriodStart(now);
        var board = Board;
        var needDaily = board.DailyPeriodStartUtc is not { } rolledDaily || rolledDaily < daily;
        var needWeekly = board.WeeklyPeriodStartUtc is not { } rolledWeekly || rolledWeekly < weekly;
        if (!needDaily && !needWeekly)
            return;

        var nextMsq = _facts.NextMsqQuestId(out var msqReady);
        // The scenario guide loads a little after login; give it a minute before rolling the day without an MSQ slot.
        if (needDaily && !msqReady && Stopwatch.GetElapsedTime(_loggedInAt) < MsqReadyWait)
            return;

        var duties = QuestContentCatalog.Duties(_data);
        var facts = _facts.Read(duties, nextMsq != 0);
        if (facts is null)
            return;

        // The MSQ the board waits on is left alone: PollMsq hands it over, crediting it first if it was just turned in.
        if (needDaily)
            QuestRollRules.ReplaceEasy(board, QuestRollRules.RollEasy(duties, facts, _rng), daily);

        if (needWeekly)
            QuestRollRules.ReplaceWeekly(board, QuestRollRules.RollWeekly(duties, facts, _rng), weekly);

        _config.Save();
        _log.Information(
            "Quest board rolled (daily={Daily}, weekly={Weekly}, slots={Slots})",
            needDaily,
            needWeekly,
            board.Slots.Count);
    }

    /// <summary>
    /// Watches the next MSQ: when the one the board waits on is turned in, it gives its wind (once ever per quest), and
    /// a sticker when today's MSQ slot hasn't given one yet. The board then waits on the new next MSQ.
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
        if (credits.Count > 0)
        {
            _log.Information("Quest board: {Count} MSQ turn-in(s) credited", credits.Count);
            Award(credits, "Main scenario progress");
            return;
        }

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
        uint territory = 0;
        try
        {
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
            cfcId, territory, contentId, synced, levelingBelowCap, periods.Rolled, periods.Daily, periods.Weekly);
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
        if (_duty is not { } watched)
            return;

        _duty = null;
        _ignoreCurrentDuty = _dutyState.IsDutyStarted;
        try
        {
            if (IsActiveCharacterReady() && watched.ContentFinderConditionId != 0 && watched.ContentId == _facts.ContentId)
                CreditClear(watched);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Quest board: crediting the clear failed");
        }
    }

    private void CreditClear(DutyObservation watched)
    {
        var clear = new QuestDutyClear(watched.ContentFinderConditionId, watched.Synced, watched.LevelingRouletteBelowCap);

        // A reset that passed during the duty waited (see OnFrameworkUpdate): the slots the board had when it began still
        // count, and the tick rolls the new ones once it ends. Any other slots are brought up to date first.
        var board = Board;
        var heldOver = watched.BoardRolledAtStart
                       && board.DailyPeriodStartUtc == watched.DailyPeriodAtStart
                       && board.WeeklyPeriodStartUtc == watched.WeeklyPeriodAtStart;
        if (!heldOver)
        {
            EnsureRolled();
            if (!IsRolled)
                return;
        }

        var credit = QuestBoardEngine.CreditDuty(board, clear);
        if (credit.Credits.Count == 0)
        {
            if (credit.UnsyncedEasyMatch)
                PluginChat.Print(_chat, "Quest board: that clear wasn't synced, so it doesn't count.", PluginChat.Orange);
            return;
        }

        _log.Information(
            "Quest board: clear credited (cfc={Cfc}, synced={Synced}, slots={Slots})",
            clear.ContentFinderConditionId,
            clear.Synced,
            credit.Credits.Count);
        Award(credit.Credits, DutyName(clear.ContentFinderConditionId));
    }

    private void Award(IReadOnlyList<QuestSlotCredit> credits, string what)
    {
        var stickers = credits.Sum(c => c.Reward.Stickers);
        var hours = credits.Sum(c => c.Reward.Hours);
        var board = Board;
        var award = QuestBoardEngine.PlaceStickers(board, stickers, hours, n => _rng.Next(n));

        if (award.TripleLine)
            _config.Achievements.TryAdd(AchievementFlags.QuestTripleLine, DateTimeOffset.UtcNow);
        var cleared = award.Placements.LastOrDefault(p => p.Lines > 0);
        if (cleared.Lines > 0)
            LastLineClear = new QuestLineClear(cleared.BoardBefore, cleared.LineCells, Stopwatch.GetTimestamp());

        // The board is saved before the wind (AddQuestWind saves too), so a failed wind never re-awards stickers.
        _config.Save();
        var windHours = award.FillToCap ? QuestRewardRules.OverwindCapHours(_config.MaxWindHours) : award.Hours;
        if (windHours > 0)
            _timer.AddQuestWind(windHours);

        PluginChat.Print(_chat, Message(what, stickers, award), PluginChat.Green);
        _log.Information(
            "Quest board award: stickers={Stickers}, lines={Lines}, fill={Fill}",
            stickers,
            award.LinesCleared,
            award.FillToCap);
    }

    /// <summary>The chat line for an award. 06: it never names hours or how much wind the doll has.</summary>
    private static string Message(string what, int stickers, QuestAward award)
    {
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
    private void OnLogout(int type, int code) => _ignoreCurrentDuty = false;
}

/// <summary>A sticker that finished a line: the board just before it cleared, the line cells, and when.</summary>
public sealed record QuestLineClear(ushort BoardBefore, ushort LineCells, long Timestamp);

/// <summary>
/// A duty being watched, as first seen: its synced and Leveling Roulette state, and the board's periods at its start
/// (so a reset during it doesn't take its slot away).
/// </summary>
internal sealed record DutyObservation(
    uint ContentFinderConditionId,
    uint TerritoryId,
    ulong ContentId,
    bool Synced,
    bool LevelingRouletteBelowCap,
    bool BoardRolledAtStart,
    DateTimeOffset? DailyPeriodAtStart,
    DateTimeOffset? WeeklyPeriodAtStart);

/// <summary>The board's reset periods at one moment, and whether it was rolled for the periods current then.</summary>
internal readonly record struct BoardPeriods(bool Rolled, DateTimeOffset? Daily, DateTimeOffset? Weekly);
