using System;

namespace WindUpKey.Quest;

/// <summary>What a quest board slot asks the doll to do.</summary>
public enum QuestSlotKind : byte
{
    None = 0,

    /// <summary>Easy: turn in the next main scenario quest. Open all day; only the first each day gives a sticker.</summary>
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
}

/// <summary>One duty on the quest board (per character, saved in <see cref="QuestBoardState"/>).</summary>
[Serializable]
public sealed class QuestSlot
{
    public QuestDifficulty Difficulty { get; set; }

    public QuestSlotKind Kind { get; set; }

    /// <summary>The ContentFinderCondition row the slot asks for; 0 for <see cref="QuestSlotKind.Msq"/> and
    /// <see cref="QuestSlotKind.LevelingRoulette"/>.</summary>
    public uint ContentFinderConditionId { get; set; }

    /// <summary>An Easy trial or normal raid: half wind for its sticker.</summary>
    public bool HalfWind { get; set; }

    /// <summary>Done for this reset. For <see cref="QuestSlotKind.Msq"/>: today's sticker was given (it still gives wind).</summary>
    public bool Completed { get; set; }

    /// <summary>A Medium or Hard slot completed unsynced (a quarter of the wind and no stickers).</summary>
    public bool CompletedUnsynced { get; set; }

    public QuestSlot Clone() => (QuestSlot)MemberwiseClone();

    /// <summary>True when the slot's tier and kind belong together (a hand-edited or damaged config fails this).</summary>
    public bool IsValid => Kind switch
    {
        QuestSlotKind.Msq or QuestSlotKind.LevelingRoulette => Difficulty == QuestDifficulty.Easy,
        QuestSlotKind.Unlock or QuestSlotKind.Duty => Difficulty == QuestDifficulty.Easy && ContentFinderConditionId != 0,
        QuestSlotKind.Extreme => Difficulty == QuestDifficulty.Medium && ContentFinderConditionId != 0,
        QuestSlotKind.Savage or QuestSlotKind.Ultimate => Difficulty == QuestDifficulty.Hard && ContentFinderConditionId != 0,
        _ => false,
    };
}
