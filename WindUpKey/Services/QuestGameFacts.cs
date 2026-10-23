using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using WindUpKey.Quest;

namespace WindUpKey.Services;

/// <summary>
/// The quest board's reads of the logged-in character: job levels, unlocked and cleared duties, the next main scenario
/// quest and level sync. A thin adapter: the rules that use these live in <see cref="QuestRollRules"/> and
/// <see cref="QuestBoardEngine"/>. Framework thread only.
/// </summary>
public sealed unsafe class QuestGameFacts
{
    /// <summary>Used when the game's own level cap can't be read.</summary>
    public const int FallbackLevelCap = 100;

    /// <summary>Quest row ids are the game's quest ids plus this.</summary>
    private const uint QuestRowOffset = 0x10000;

    /// <summary>Quest.PreviousQuestJoin: any one of the previous quests is enough.</summary>
    private const byte PreviousQuestJoinAny = 2;

    private readonly IDataManager _data;
    private readonly IPlayerState _player;
    private List<ClassJob>? _combatJobs;

    public QuestGameFacts(IDataManager data, IPlayerState player)
    {
        _data = data;
        _player = player;
    }

    public bool IsLoaded => _player.IsLoaded;

    /// <summary>The logged-in character's ContentId (0 when not loaded).</summary>
    public ulong ContentId => _player.IsLoaded ? _player.ContentId : 0;

    /// <summary>The level cap of the installed game.</summary>
    public int LevelCap
    {
        get
        {
            var state = PlayerState.Instance();
            return state is not null && state->MaxLevel > 0 ? state->MaxLevel : FallbackLevelCap;
        }
    }

    /// <summary>What the rolls need, or null when the character isn't loaded.</summary>
    public QuestCharacterFacts? Read(IReadOnlyList<QuestDutyRow> duties, bool hasNextMsq)
    {
        if (!_player.IsLoaded)
            return null;

        var cap = LevelCap;
        var levels = CombatJobs()
            .GroupBy(j => j.ExpArrayIndex)
            .Select(g => (int)_player.GetClassJobLevel(g.First()))
            .Where(l => l > 0)
            .ToList();
        var highest = levels.Count == 0 ? 0 : levels.Max();

        var unlocked = new HashSet<uint>();
        var unlockable = new HashSet<uint>();
        var cleared = new HashSet<uint>();
        var clearedHighEnd = new HashSet<uint>();
        foreach (var duty in duties)
        {
            var instance = QuestContentCatalog.InstanceContentId(_data, duty.ContentFinderConditionId);
            if (instance == 0)
                continue;

            if (duty.Category == QuestDutyCategory.Ultimate)
            {
                if (UIState.IsInstanceContentCompleted(instance))
                    cleared.Add(duty.ContentFinderConditionId);
                continue;
            }

            if (duty.Category is QuestDutyCategory.Extreme or QuestDutyCategory.Savage && UIState.IsInstanceContentCompleted(instance))
                clearedHighEnd.Add(duty.ContentFinderConditionId);

            if (UIState.IsInstanceContentUnlocked(instance))
                unlocked.Add(duty.ContentFinderConditionId);
            else if (duty.InMentorPool && duty.Level <= highest && CanTakeAnUnlockQuest(instance, highest))
                unlockable.Add(duty.ContentFinderConditionId);
        }

        return new QuestCharacterFacts(highest, levels.Any(l => l < cap), hasNextMsq, unlocked, unlockable, cleared, clearedHighEnd);
    }

    /// <summary>
    /// The character has never cleared duty <paramref name="contentFinderConditionId"/> (false when it can't be told: a
    /// duty the board doesn't know). Read as a duty starts: the game marks it completed only at the clear.
    /// </summary>
    public bool IsUncleared(uint contentFinderConditionId)
    {
        var instance = QuestContentCatalog.InstanceContentId(_data, contentFinderConditionId);
        return instance != 0 && !UIState.IsInstanceContentCompleted(instance);
    }

    /// <summary>
    /// The next main scenario quest (Quest row id), 0 when the story is done. <paramref name="ready"/> is false while the
    /// game hasn't loaded the scenario guide yet (the answer means nothing then).
    /// </summary>
    public uint NextMsqQuestId(out bool ready)
    {
        ready = false;
        var agent = AgentScenarioTree.Instance();
        if (agent is null || agent->Data is null)
            return 0;

        ready = true;
        var data = agent->Data;
        var paths = data->MainScenarioQuestIds;
        var index = data->MSQPathIndex < paths.Length ? data->MSQPathIndex : 0;
        uint id = paths[index];
        if (id == 0)
            return 0;

        var questId = id < QuestRowOffset ? id + QuestRowOffset : id;
        return IsQuestComplete(questId) ? 0 : questId;
    }

    public bool IsQuestComplete(uint questId) => questId != 0 && QuestManager.IsQuestComplete(questId);

    /// <summary>The current job is a combat job (not a limited job) below the level cap.</summary>
    public bool IsCurrentJobBelowCap()
    {
        if (!_player.IsLoaded || !_player.ClassJob.IsValid)
            return false;

        var job = _player.ClassJob.Value;
        if (!IsCombatJob(job))
            return false;

        var level = _player.GetClassJobLevel(job);
        return level > 0 && level < LevelCap;
    }

    /// <summary>
    /// Synced for the duty: the game synced the character's level, or their level is already at or below the duty's sync
    /// level. Unrestricted Party counts only with its Level Sync option on, even at the duty's level (item level isn't
    /// synced then): <paramref name="unrestrictedWithoutLevelSync"/>, from <see cref="ReadQueue"/>.
    /// </summary>
    public bool IsSyncedFor(ContentFinderCondition cfc, bool unrestrictedWithoutLevelSync)
    {
        if (unrestrictedWithoutLevelSync)
            return false;

        if (_player.IsLevelSynced || cfc.ClassJobLevelSync == 0)
            return true;
        if (!_player.ClassJob.IsValid)
            return true;

        return _player.GetClassJobLevel(_player.ClassJob.Value) <= cfc.ClassJobLevelSync;
    }

    /// <summary>
    /// How the Duty Finder put the character in their current duty: the queued roulette and the popped content's
    /// Unrestricted Party options. Read only while the queue says it is in content, so a pop that was declined or
    /// withdrawn, or a duty entered another way (Duty Support), reads as no roulette and no Unrestricted Party. The
    /// popped options are the queue's own, not the Duty Finder checkboxes, which stay as last set.
    /// </summary>
    public QueueFacts ReadQueue()
    {
        var finder = ContentsFinder.Instance();
        if (finder is null || finder->QueueInfo.QueueState != ContentsFinderQueueState.InContent)
            return default;

        ref readonly var info = ref finder->QueueInfo;
        return new QueueFacts(
            info.QueuedContentRouletteId,
            info.PoppedContentIsUnrestrictedParty && !info.PoppedContentIsLevelSync);
    }

    /// <summary>Of <paramref name="questIds"/>, the ones the character has completed.</summary>
    public IEnumerable<uint> Completed(IEnumerable<uint> questIds) => questIds.Where(IsQuestComplete);

    private bool CanTakeAnUnlockQuest(uint instanceContentId, int highestLevel)
    {
        var sheet = _data.GetExcelSheet<Lumina.Excel.Sheets.Quest>(ClientLanguage.English);
        if (sheet is null)
            return false;

        foreach (var questId in QuestContentCatalog.UnlockQuests(_data, instanceContentId))
        {
            if (IsQuestComplete(questId) || !sheet.TryGetRow(questId, out var quest))
                continue;
            if (quest.ClassJobLevel[0] > highestLevel)
                continue;
            if (quest.GrandCompany.RowId != 0 && quest.GrandCompany.RowId != _player.GrandCompany.RowId)
                continue;
            if (PreviousQuestsDone(quest))
                return true;
        }

        return false;
    }

    private bool PreviousQuestsDone(Lumina.Excel.Sheets.Quest quest)
    {
        var previous = quest.PreviousQuest.Select(q => q.RowId).Where(id => id != 0).ToList();
        if (previous.Count == 0)
            return true;

        return quest.PreviousQuestJoin == PreviousQuestJoinAny
            ? previous.Any(IsQuestComplete)
            : previous.All(IsQuestComplete);
    }

    private static bool IsCombatJob(ClassJob job) =>
        job.RowId != 0 && job.DohDolJobIndex < 0 && job.ExpArrayIndex >= 0 && !job.IsLimitedJob;

    private List<ClassJob> CombatJobs()
    {
        if (_combatJobs is not null)
            return _combatJobs;

        // Only ids and flags are read; English like the catalog.
        var sheet = _data.GetExcelSheet<ClassJob>(ClientLanguage.English);
        _combatJobs = sheet is null ? [] : sheet.Where(IsCombatJob).ToList();
        return _combatJobs;
    }
}

/// <summary>What the Duty Finder queue says about the current duty (default: not entered through the Duty Finder).</summary>
/// <param name="RouletteId">The queued ContentRoulette row, or 0.</param>
/// <param name="UnrestrictedWithoutLevelSync">Unrestricted Party with its Level Sync option off.</param>
public readonly record struct QueueFacts(uint RouletteId, bool UnrestrictedWithoutLevelSync);
