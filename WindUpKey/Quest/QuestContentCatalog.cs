using System;
using System.Collections.Generic;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace WindUpKey.Quest;

/// <summary>
/// The duties the quest board can offer, classified from the English Excel sheets so the name matches work on every
/// client (11-D4). The sheet's <c>HighEndDuty</c> flag only marks the current tier, so Extremes, Savage and Ultimates
/// are told apart by content type and English name instead. New tiers need no code change.
/// </summary>
public static class QuestContentCatalog
{
    /// <summary>Lumina ContentType rows.</summary>
    public const uint DungeonsContentType = 2;

    public const uint GuildhestsContentType = 3;

    public const uint TrialsContentType = 4;

    public const uint RaidsContentType = 5;

    public const uint UltimateRaidsContentType = 28;

    /// <summary>ContentFinderCondition.ContentLinkType for an InstanceContent row.</summary>
    public const byte InstanceContentLink = 1;

    /// <summary>The quest script parameter that names an InstanceContent the quest unlocks (INSTANCEDUNGEON0, 1, …).</summary>
    public const string UnlockScriptNeedle = "INSTANCEDUNGEON";

    /// <summary>The English ContentRoulette name fragment of the Leveling Roulette.</summary>
    public const string LevelingRouletteNeedle = "Leveling";

    private static List<QuestDutyRow>? _duties;
    private static Dictionary<uint, uint>? _instanceContentByCfc;
    private static Dictionary<uint, List<uint>>? _unlockQuestsByInstance;
    private static HashSet<uint>? _levelingRouletteIds;
    private static List<uint>? _mainScenarioQuestIds;

    /// <summary>The English JournalSection name fragment of the main scenario sections.</summary>
    public const string MainScenarioSectionNeedle = "Main Scenario";

    /// <summary>Every main scenario quest (Quest rows in a journal section named "Main Scenario …"), cached.</summary>
    public static IReadOnlyList<uint> MainScenarioQuestIds(IDataManager data)
    {
        if (_mainScenarioQuestIds is not null)
            return _mainScenarioQuestIds;

        var sections = new HashSet<uint>();
        // 11-D4: English, so the needle matches on every client.
        var sectionSheet = data.GetExcelSheet<JournalSection>(ClientLanguage.English);
        if (sectionSheet is not null)
        {
            foreach (var section in sectionSheet)
            {
                if (section.Name.ExtractText().StartsWith(MainScenarioSectionNeedle, StringComparison.OrdinalIgnoreCase))
                    sections.Add(section.RowId);
            }
        }

        var ids = new List<uint>();
        var questSheet = data.GetExcelSheet<Lumina.Excel.Sheets.Quest>(ClientLanguage.English);
        if (questSheet is not null && sections.Count > 0)
        {
            foreach (var quest in questSheet)
            {
                if (quest.JournalGenre.RowId == 0 || !quest.JournalGenre.IsValid)
                    continue;
                var category = quest.JournalGenre.Value.JournalCategory;
                if (category.IsValid && sections.Contains(category.Value.JournalSection.RowId))
                    ids.Add(quest.RowId);
            }
        }

        _mainScenarioQuestIds = ids;
        return ids;
    }

    /// <summary>Every duty the board knows (cached).</summary>
    public static IReadOnlyList<QuestDutyRow> Duties(IDataManager data)
    {
        EnsureDutyCache(data);
        return _duties!;
    }

    /// <summary>The InstanceContent row of a duty, or 0.</summary>
    public static uint InstanceContentId(IDataManager data, uint contentFinderConditionId)
    {
        EnsureDutyCache(data);
        return _instanceContentByCfc!.GetValueOrDefault(contentFinderConditionId);
    }

    /// <summary>The quests that unlock an InstanceContent row (none when another way unlocks it).</summary>
    public static IReadOnlyList<uint> UnlockQuests(IDataManager data, uint instanceContentId)
    {
        EnsureUnlockQuestCache(data);
        return _unlockQuestsByInstance!.TryGetValue(instanceContentId, out var quests) ? quests : [];
    }

    public static bool IsLevelingRoulette(IDataManager data, uint contentRouletteId)
    {
        if (contentRouletteId == 0)
            return false;

        EnsureRouletteCache(data);
        return _levelingRouletteIds!.Contains(contentRouletteId);
    }

    /// <summary>What kind of duty an English ContentFinderCondition row is.</summary>
    public static QuestDutyCategory Classify(ContentFinderCondition cfc, string englishName)
    {
        switch (cfc.ContentType.RowId)
        {
            case UltimateRaidsContentType:
                return QuestDutyCategory.Ultimate;
            case RaidsContentType:
                if (englishName.Contains("(Savage)", StringComparison.OrdinalIgnoreCase))
                    return QuestDutyCategory.Savage;
                if (englishName.Contains("(Chaotic)", StringComparison.OrdinalIgnoreCase))
                    return QuestDutyCategory.Other;
                return cfc.AllianceRoulette ? QuestDutyCategory.AllianceRaid : QuestDutyCategory.NormalRaid;
            case TrialsContentType:
                if (englishName.Contains("(Unreal)", StringComparison.OrdinalIgnoreCase))
                    return QuestDutyCategory.Other;
                if (englishName.Contains("(Extreme)", StringComparison.OrdinalIgnoreCase)
                    || englishName.Contains("Minstrel's Ballad", StringComparison.OrdinalIgnoreCase))
                    return QuestDutyCategory.Extreme;
                return QuestDutyCategory.Trial;
            case DungeonsContentType:
                return QuestDutyCategory.Dungeon;
            case GuildhestsContentType:
                return QuestDutyCategory.Guildhest;
            default:
                return QuestDutyCategory.Other;
        }
    }

    public static void InvalidateCaches()
    {
        _duties = null;
        _instanceContentByCfc = null;
        _unlockQuestsByInstance = null;
        _levelingRouletteIds = null;
        _mainScenarioQuestIds = null;
    }

    private static void EnsureDutyCache(IDataManager data)
    {
        if (_duties is not null && _instanceContentByCfc is not null)
            return;

        var duties = new List<QuestDutyRow>();
        var instances = new Dictionary<uint, uint>();
        // 11-D4: English names on every client, so the name matches work on Japanese, German and French clients.
        var sheet = data.GetExcelSheet<ContentFinderCondition>(ClientLanguage.English);
        if (sheet is not null)
        {
            foreach (var row in sheet)
            {
                if (row.RowId == 0 || row.PvP || row.ContentLinkType != InstanceContentLink || row.Content.RowId == 0)
                    continue;

                var name = row.Name.ExtractText();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var category = Classify(row, name);
                if (category == QuestDutyCategory.Other)
                    continue;

                duties.Add(new QuestDutyRow(row.RowId, category, row.ClassJobLevelRequired, row.MentorRoulette, row.SortKey, row.HighEndDuty));
                instances[row.RowId] = row.Content.RowId;
            }
        }

        _instanceContentByCfc = instances;
        _duties = duties;
    }

    private static void EnsureUnlockQuestCache(IDataManager data)
    {
        if (_unlockQuestsByInstance is not null)
            return;

        // Few duties name their unlock in InstanceContentUnlock: most are unlocked by an INSTANCEDUNGEON* script
        // parameter. A quest's first one (INSTANCEDUNGEON0) is its own duty; later ones are often only references (a
        // relic or Omega step naming an older duty), so they count only for a duty no quest unlocks the first way.
        var primary = new Dictionary<uint, List<uint>>();
        var secondary = new Dictionary<uint, List<uint>>();
        static void Add(Dictionary<uint, List<uint>> map, uint instance, uint questId)
        {
            if (instance == 0)
                return;
            if (!map.TryGetValue(instance, out var quests))
                map[instance] = quests = [];
            if (!quests.Contains(questId))
                quests.Add(questId);
        }

        // Only ids and script names are read; English for the same sheet as the rest of the catalog.
        var sheet = data.GetExcelSheet<Lumina.Excel.Sheets.Quest>(ClientLanguage.English);
        if (sheet is not null)
        {
            foreach (var quest in sheet)
            {
                Add(primary, quest.InstanceContentUnlock.RowId, quest.RowId);
                foreach (var param in quest.QuestParams)
                {
                    var instruction = param.ScriptInstruction.ExtractText();
                    if (!instruction.StartsWith(UnlockScriptNeedle, StringComparison.Ordinal))
                        continue;
                    Add(instruction == UnlockScriptNeedle + "0" ? primary : secondary, param.ScriptArg, quest.RowId);
                }
            }
        }

        foreach (var (instance, quests) in secondary)
            primary.TryAdd(instance, quests);
        _unlockQuestsByInstance = primary;
    }

    private static void EnsureRouletteCache(IDataManager data)
    {
        if (_levelingRouletteIds is not null)
            return;

        var ids = new HashSet<uint>();
        // 11-D4: English, as for the duties (the needle is the English roulette name).
        var sheet = data.GetExcelSheet<ContentRoulette>(ClientLanguage.English);
        if (sheet is not null)
        {
            foreach (var row in sheet)
            {
                if (row.RowId != 0 && row.Name.ExtractText().Contains(LevelingRouletteNeedle, StringComparison.OrdinalIgnoreCase))
                    ids.Add(row.RowId);
            }
        }

        _levelingRouletteIds = ids;
    }
}
