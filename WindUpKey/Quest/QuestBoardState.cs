using System;
using System.Collections.Generic;
using System.Linq;

namespace WindUpKey.Quest;

/// <summary>
/// A doll character's quest board: the 4×4 sticker board, this reset's slots and the weekly rerolls. Saved per character
/// (<see cref="CharacterProfile.QuestBoard"/>, mirrored in <see cref="Configuration.QuestBoard"/>). No Dalamud dependency.
/// </summary>
[Serializable]
public sealed class QuestBoardState
{
    /// <summary>The sticker board: bit <c>row * 4 + column</c> is set when that cell has a sticker.</summary>
    public ushort Stickers { get; set; }

    /// <summary>The daily period (<see cref="QuestResetClock.DailyPeriodStart"/>) the Daily slot was rolled for.</summary>
    public DateTimeOffset? DailyPeriodStartUtc { get; set; }

    /// <summary>The weekly period (<see cref="QuestResetClock.WeeklyPeriodStart"/>) the Easy, Medium and Hard slots were rolled for.</summary>
    public DateTimeOffset? WeeklyPeriodStartUtc { get; set; }

    /// <summary>The board's rules when its Easy slots were rolled (<see cref="CurrentRules"/>; older boards roll Easy again).</summary>
    public int RulesVersion { get; set; }

    /// <summary>2 (0.2.10.0): Daily roulettes, a weekly Easy set of dungeons and guildhests, no MSQ slot.</summary>
    public const int CurrentRules = 2;

    /// <summary>The daily period whose Easy reroll was used (Easy rerolls once a day), or null.</summary>
    public DateTimeOffset? EasyRerollDailyPeriodUtc { get; set; }

    /// <summary>This reset's slots, all tiers (Daily first, then Easy, Medium and Hard).</summary>
    public List<QuestSlot> Slots { get; set; } = [];

    /// <summary>The Medium reroll was used this week.</summary>
    public bool MediumRerollUsed { get; set; }

    /// <summary>The Hard reroll was used this week.</summary>
    public bool HardRerollUsed { get; set; }

    /// <summary>The main scenario quest the board is waiting on (Quest row), or 0. Its turn-in is the MSQ slot's credit.</summary>
    public uint MsqTrackedQuestId { get; set; }

    /// <summary>
    /// Main scenario quests that can't give wind again: every one credited, plus every one already done when the board
    /// was first set up (<see cref="MsqHistorySeeded"/>), so New Game+ replays of the story earn nothing.
    /// </summary>
    public List<uint> CreditedMsqQuestIds { get; set; } = [];

    /// <summary>The main scenario quests done before the board existed were added to <see cref="CreditedMsqQuestIds"/>.</summary>
    public bool MsqHistorySeeded { get; set; }

    /// <summary>
    /// Lines the relay hasn't paid in springs yet, in batches it settles by requestId. A batch is resent until the relay
    /// answers (a disconnect never loses one), and it pays each id once.
    /// </summary>
    public List<PendingSpringGrant> PendingSpringGrants { get; set; } = [];

    public QuestBoardState Clone() => new()
    {
        Stickers = Stickers,
        DailyPeriodStartUtc = DailyPeriodStartUtc,
        WeeklyPeriodStartUtc = WeeklyPeriodStartUtc,
        RulesVersion = RulesVersion,
        EasyRerollDailyPeriodUtc = EasyRerollDailyPeriodUtc,
        Slots = (Slots ?? []).Where(s => s is not null).Select(s => s.Clone()).ToList(),
        MediumRerollUsed = MediumRerollUsed,
        HardRerollUsed = HardRerollUsed,
        MsqTrackedQuestId = MsqTrackedQuestId,
        CreditedMsqQuestIds = (CreditedMsqQuestIds ?? []).ToList(),
        MsqHistorySeeded = MsqHistorySeeded,
        PendingSpringGrants = (PendingSpringGrants ?? []).Where(g => g is not null).Select(g => g.Clone()).ToList(),
    };

    /// <summary>Drops null or invalid slots (a damaged or hand-edited config). Returns true when anything changed.</summary>
    public bool Normalize()
    {
        Slots ??= [];
        CreditedMsqQuestIds ??= [];
        PendingSpringGrants ??= [];
        PendingSpringGrants.RemoveAll(g => g is null || g.Lines <= 0 || string.IsNullOrEmpty(g.RequestId));
        return Slots.RemoveAll(s => s is null || !s.IsValid) > 0;
    }

    public IEnumerable<QuestSlot> SlotsOf(QuestDifficulty difficulty) => Slots.Where(s => s.Difficulty == difficulty);
}

/// <summary>Quest board lines waiting for the relay to pay them in springs.</summary>
[Serializable]
public sealed class PendingSpringGrant
{
    public string RequestId { get; set; } = string.Empty;

    public int Lines { get; set; }

    /// <summary>Sent at least once: it no longer takes new lines, so the relay's answer for it covers exactly these.</summary>
    public bool Sent { get; set; }

    public PendingSpringGrant Clone() => (PendingSpringGrant)MemberwiseClone();
}
