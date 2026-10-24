namespace WindUpKey.Quest;

/// <summary>
/// The tier of a quest board slot: Daily resets daily, and Easy, Medium and Hard weekly. <see cref="None"/> is never a
/// valid slot (a config with it drops the slot).
/// </summary>
public enum QuestDifficulty : byte
{
    None = 0,
    Easy = 1,
    Medium = 2,
    Hard = 3,
    Daily = 4,
}
