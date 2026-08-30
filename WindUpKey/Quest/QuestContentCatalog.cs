using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace WindUpKey.Quest;

/// <summary>
/// Classifies duties for daily quests. Roulette allowlist and current savage tier are
/// resolved from Excel sheets (name match) so daily-complete flags are never used.
/// Update <see cref="CurrentSavageNameNeedle"/> when the savage tier rotates.
/// </summary>
public static class QuestContentCatalog
{
    /// <summary>Dawntrail. Bump when the expansion changes.</summary>
    public const uint CurrentExVersion = 5;

    /// <summary>Lumina ContentType row for Trials.</summary>
    public const uint TrialsContentType = 4;

    /// <summary>
    /// English name fragment for the current savage tier (Heavyweight M9S–M12S).
    /// Matched against ContentFinderCondition.Name together with "(Savage)".
    /// </summary>
    public const string CurrentSavageNameNeedle = "AAC Heavyweight";

    public const int EasyRouletteRequired = 2;
    public const int MediumExtremeRequired = 3;

    public const double EasyRewardHours = 24;
    public const double MediumRewardHours = 36;
    public const double HardRewardHours = 48;

    public static readonly TimeSpan QuestLockDuration = TimeSpan.FromHours(24);

    private static readonly string[] AllowedRouletteNeedles =
    [
        "Expert",
        "Level Cap",
        "Leveling",
        "Main Scenario",
        "Alliance",
    ];

    private static HashSet<uint>? _allowedRouletteIds;
    private static HashSet<uint>? _currentSavageCfcIds;

    public static double RewardHours(QuestDifficulty difficulty) => difficulty switch
    {
        QuestDifficulty.Easy => EasyRewardHours,
        QuestDifficulty.Medium => MediumRewardHours,
        QuestDifficulty.Hard => HardRewardHours,
        _ => 0,
    };

    public static bool IsAllowedRoulette(IDataManager data, uint contentRouletteId)
    {
        if (contentRouletteId == 0)
            return false;

        EnsureRouletteCache(data);
        return _allowedRouletteIds!.Contains(contentRouletteId);
    }

    public static bool IsCurrentExpansionExtreme(ContentFinderCondition cfc)
    {
        if (!cfc.HighEndDuty)
            return false;
        if (cfc.ContentType.RowId != TrialsContentType)
            return false;
        return cfc.RequiredExVersion.RowId == CurrentExVersion;
    }

    public static bool IsCurrentTierSavage(IDataManager data, ContentFinderCondition cfc)
    {
        if (!cfc.HighEndDuty)
            return false;

        EnsureSavageCache(data);
        return _currentSavageCfcIds!.Contains(cfc.RowId);
    }

    public static void InvalidateCaches()
    {
        _allowedRouletteIds = null;
        _currentSavageCfcIds = null;
    }

    private static void EnsureRouletteCache(IDataManager data)
    {
        if (_allowedRouletteIds is not null)
            return;

        var ids = new HashSet<uint>();
        var sheet = data.GetExcelSheet<ContentRoulette>();
        if (sheet is null)
        {
            _allowedRouletteIds = ids;
            return;
        }

        foreach (var row in sheet)
        {
            if (row.RowId == 0)
                continue;

            var name = row.Name.ExtractText();
            var dutyType = row.DutyType.ExtractText();
            var haystack = $"{name} {dutyType}";
            foreach (var needle in AllowedRouletteNeedles)
            {
                if (haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    ids.Add(row.RowId);
                    break;
                }
            }
        }

        _allowedRouletteIds = ids;
    }

    private static void EnsureSavageCache(IDataManager data)
    {
        if (_currentSavageCfcIds is not null)
            return;

        var ids = new HashSet<uint>();
        var sheet = data.GetExcelSheet<ContentFinderCondition>();
        if (sheet is null)
        {
            _currentSavageCfcIds = ids;
            return;
        }

        foreach (var row in sheet)
        {
            if (!row.HighEndDuty)
                continue;

            var name = row.Name.ExtractText();
            if (!name.Contains(CurrentSavageNameNeedle, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!name.Contains("(Savage)", StringComparison.OrdinalIgnoreCase))
                continue;

            ids.Add(row.RowId);
        }

        _currentSavageCfcIds = ids;
    }
}
