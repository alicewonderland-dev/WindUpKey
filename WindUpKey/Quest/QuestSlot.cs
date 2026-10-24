using System;

namespace WindUpKey.Quest;

/// <summary>What a quest board slot asks the doll to do.</summary>
public enum QuestSlotKind : byte
{
    None = 0,

    /// <summary>Retired (0.2.10.0): the Easy main scenario slot. A saved one is dropped; turn-ins give wind only.</summary>
    Msq = 1,

    /// <summary>Easy: clear (synced) a mentor-roulette duty the character can unlock but hasn't yet.</summary>
    Unlock = 2,

    /// <summary>Easy: a Leveling Roulette on a job below the level cap.</summary>
    LevelingRoulette = 3,

    /// <summary>Easy: clear (synced) an unlocked mentor-roulette duty.</summary>
    Duty = 4,

    /// <summary>Medium: an Extreme trial from the character's bracket.</summary>
    Extreme = 5,

    /// <summary>Hard: a Savage raid from the character's bracket.</summary>
    Savage = 6,

    /// <summary>Hard: an Ultimate raid the character has cleared before.</summary>
    Ultimate = 7,

    /// <summary>Daily: complete <see cref="QuestSlot.Target"/> roulettes (any that count, the same one more than once).</summary>
    Roulettes = 8,
}

/// <summary>One duty on the quest board (per character, saved in <see cref="QuestBoardState"/>).</summary>
[Serializable]
public sealed class QuestSlot
{
    public QuestDifficulty Difficulty { get; set; }

    public QuestSlotKind Kind { get; set; }

    /// <summary>The ContentFinderCondition row the slot asks for; 0 for <see cref="QuestSlotKind.LevelingRoulette"/> and
    /// <see cref="QuestSlotKind.Roulettes"/>.</summary>
    public uint ContentFinderConditionId { get; set; }

    /// <summary>Retired with the Easy trials and normal raids (0.2.10.0); kept so older configs still read.</summary>
    public bool HalfWind { get; set; }

    /// <summary>Done for this reset.</summary>
    public bool Completed { get; set; }

    /// <summary>A Medium or Hard slot completed unsynced (a quarter of the wind and no stickers).</summary>
    public bool CompletedUnsynced { get; set; }

    /// <summary><see cref="QuestSlotKind.Roulettes"/>: how many count so far, and how many finish it.</summary>
    public int Progress { get; set; }

    public int Target { get; set; }

    public QuestSlot Clone() => (QuestSlot)MemberwiseClone();

    /// <summary>True when the slot's tier and kind belong together (a hand-edited or damaged config fails this).</summary>
    public bool IsValid => Kind switch
    {
        QuestSlotKind.LevelingRoulette => Difficulty == QuestDifficulty.Easy,
        QuestSlotKind.Unlock or QuestSlotKind.Duty => Difficulty == QuestDifficulty.Easy && ContentFinderConditionId != 0,
        QuestSlotKind.Extreme => Difficulty == QuestDifficulty.Medium && ContentFinderConditionId != 0,
        QuestSlotKind.Savage or QuestSlotKind.Ultimate => Difficulty == QuestDifficulty.Hard && ContentFinderConditionId != 0,
        QuestSlotKind.Roulettes => Difficulty == QuestDifficulty.Daily && Target > 0,
        // The retired MSQ slot (and anything unknown) is dropped.
        _ => false,
    };
}
