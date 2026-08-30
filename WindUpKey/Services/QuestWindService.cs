using System;
using Dalamud.Game.DutyState;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using WindUpKey.Quest;
using WindUpKey.Sources;

namespace WindUpKey.Services;

/// <summary>
/// Doll-only daily quests: accept difficulty, track roulette / Extreme / Savage clears,
/// award uncapped wind once requirements are met.
/// </summary>
public sealed class QuestWindService : IWindUpSource
{
    private readonly Configuration _config;
    private readonly WindTimerService _timer;
    private readonly IDutyState _dutyState;
    private readonly IDataManager _data;
    private readonly IClientState _clientState;
    private readonly IChatGui _chat;
    private readonly IPluginLog _log;

    private bool _enabled;
    private bool _tracking;
    private bool _completed;
    private bool _ignoreCurrentDuty;
    private uint _queuedRouletteId;
    private uint _contentFinderConditionId;

    public QuestWindService(
        Configuration config,
        WindTimerService timer,
        IDutyState dutyState,
        IDataManager data,
        IClientState clientState,
        IChatGui chat,
        IPluginLog log)
    {
        _config = config;
        _timer = timer;
        _dutyState = dutyState;
        _data = data;
        _clientState = clientState;
        _chat = chat;
        _log = log;
    }

    public void Enable()
    {
        if (_enabled)
            return;

        _dutyState.DutyStarted += OnDutyStarted;
        _dutyState.DutyCompleted += OnDutyCompleted;
        _clientState.Logout += OnLogout;
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
        _enabled = false;
        ResetDuty();
    }

    public bool HasActiveQuest =>
        _config.IsDoll && _config.QuestDifficulty != QuestDifficulty.None;

    public bool IsQuestLockExpired()
    {
        if (_config.QuestAcceptedAtUtc is not { } accepted)
            return true;
        return DateTimeOffset.UtcNow >= accepted + QuestContentCatalog.QuestLockDuration;
    }

    /// <summary>Time until a new difficulty may be selected; zero when selectable.</summary>
    public TimeSpan QuestLockRemaining()
    {
        if (_config.QuestAcceptedAtUtc is not { } accepted)
            return TimeSpan.Zero;
        var until = accepted + QuestContentCatalog.QuestLockDuration - DateTimeOffset.UtcNow;
        return until > TimeSpan.Zero ? until : TimeSpan.Zero;
    }

    /// <summary>
    /// Doll may pick a difficulty when under/at max wind and either has no quest
    /// or the 24h accept lock has expired.
    /// </summary>
    public bool CanSelectDifficulty(out string? blockReason)
    {
        blockReason = null;
        if (!_config.IsDoll)
        {
            blockReason = "Quests are only available for dolls.";
            return false;
        }

        if (_timer.IsAboveMaxWind())
        {
            blockReason = "You are above your max wind hours and cannot accept a quest.";
            return false;
        }

        if (HasActiveQuest && !IsQuestLockExpired())
        {
            blockReason = "Your current quest is locked in. You can change difficulty after the lock ends.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Accepts <paramref name="difficulty"/>, wiping any previous progress.
    /// Returns false when selection is blocked.
    /// </summary>
    public bool TryAccept(QuestDifficulty difficulty, out string? error)
    {
        error = null;
        if (difficulty is < QuestDifficulty.Easy or > QuestDifficulty.Hard)
        {
            error = "Choose Easy, Medium, or Hard.";
            return false;
        }

        if (!CanSelectDifficulty(out error))
            return false;

        _config.QuestDifficulty = difficulty;
        _config.QuestAcceptedAtUtc = DateTimeOffset.UtcNow;
        WipeProgressFields();
        _config.Save();

        PluginChat.Print(
            _chat,
            $"Quest accepted: {DifficultyLabel(difficulty)}. Complete the requirements for {QuestContentCatalog.RewardHours(difficulty):0} hours of winding.",
            PluginChat.Green);
        return true;
    }

    public bool RequirementsMet(QuestDifficulty difficulty)
    {
        if (_config.QuestRouletteClears < QuestContentCatalog.EasyRouletteRequired)
            return false;

        if (difficulty >= QuestDifficulty.Medium
            && _config.QuestExtremeClears < QuestContentCatalog.MediumExtremeRequired)
            return false;

        if (difficulty >= QuestDifficulty.Hard && !_config.QuestSavageCleared)
            return false;

        return true;
    }

    private void WipeProgressFields()
    {
        _config.QuestRouletteClears = 0;
        _config.QuestExtremeClears = 0;
        _config.QuestSavageCleared = false;
        _config.QuestRewardClaimed = false;
    }

    private void OnDutyStarted(IDutyStateEventArgs args) => BeginObservedDuty();

    private void BeginObservedDuty()
    {
        _ignoreCurrentDuty = false;
        _tracking = true;
        _completed = false;
        _queuedRouletteId = TryReadQueuedRouletteId();
        _contentFinderConditionId = _dutyState.ContentFinderCondition.RowId;
    }

    private void OnDutyCompleted(IDutyStateEventArgs args)
    {
        if (!_tracking || _completed || _ignoreCurrentDuty)
            return;

        _completed = true;

        if (!_config.IsDoll || !HasActiveQuest || _config.QuestRewardClaimed)
        {
            ResetDuty();
            return;
        }

        var changed = false;
        if (_queuedRouletteId != 0
            && QuestContentCatalog.IsAllowedRoulette(_data, _queuedRouletteId)
            && _config.QuestRouletteClears < QuestContentCatalog.EasyRouletteRequired)
        {
            _config.QuestRouletteClears++;
            changed = true;
            _log.Information(
                "Quest roulette clear credited (rouletteId={RouletteId}, clears={Clears})",
                _queuedRouletteId,
                _config.QuestRouletteClears);
        }

        if (_contentFinderConditionId != 0
            && _data.GetExcelSheet<ContentFinderCondition>()?.TryGetRow(_contentFinderConditionId, out var cfc) == true)
        {
            if (_config.QuestDifficulty >= QuestDifficulty.Medium
                && QuestContentCatalog.IsCurrentExpansionExtreme(cfc))
            {
                _config.QuestExtremeClears++;
                changed = true;
                _log.Information(
                    "Quest Extreme clear credited (cfc={Cfc}, clears={Clears})",
                    _contentFinderConditionId,
                    _config.QuestExtremeClears);
            }

            if (_config.QuestDifficulty >= QuestDifficulty.Hard
                && !_config.QuestSavageCleared
                && QuestContentCatalog.IsCurrentTierSavage(_data, cfc))
            {
                _config.QuestSavageCleared = true;
                changed = true;
                _log.Information(
                    "Quest Savage clear credited (cfc={Cfc})",
                    _contentFinderConditionId);
            }
        }

        if (changed)
            _config.Save();

        TryAwardIfComplete();
        ResetDuty();
        _ignoreCurrentDuty = _dutyState.IsDutyStarted;
    }

    private void TryAwardIfComplete()
    {
        if (!_config.IsDoll || _config.QuestRewardClaimed)
            return;

        var difficulty = _config.QuestDifficulty;
        if (difficulty == QuestDifficulty.None || !RequirementsMet(difficulty))
            return;

        var hours = QuestContentCatalog.RewardHours(difficulty);
        _config.QuestRewardClaimed = true;
        _config.Save();

        var added = _timer.AddQuestWind(hours);
        if (added > TimeSpan.Zero)
        {
            PluginChat.Print(
                _chat,
                $"Quest complete ({DifficultyLabel(difficulty)}) — the key turns with fresh wind.",
                PluginChat.Green);
            _log.Information("Quest wind awarded: {Hours}h for {Difficulty}", hours, difficulty);
        }
    }

    private static unsafe uint TryReadQueuedRouletteId()
    {
        var finder = ContentsFinder.Instance();
        if (finder is null)
            return 0;
        return finder->QueueInfo.QueuedContentRouletteId;
    }

    private void OnLogout(int type, int code) => ResetDuty();

    private void ResetDuty()
    {
        _tracking = false;
        _completed = false;
        _queuedRouletteId = 0;
        _contentFinderConditionId = 0;
    }

    public static string DifficultyLabel(QuestDifficulty difficulty) => difficulty switch
    {
        QuestDifficulty.Easy => "Easy",
        QuestDifficulty.Medium => "Medium",
        QuestDifficulty.Hard => "Hard",
        _ => "None",
    };
}
