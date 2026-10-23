namespace WindUpKey;

/// <summary>
/// Ids of the flags in <see cref="CharacterProfile.Achievements"/>, kept for an achievement system to come. A flag is
/// set once, with the time it was first earned, and never cleared.
/// </summary>
public static class AchievementFlags
{
    /// <summary>One sticker finished three or more quest board lines at once.</summary>
    public const string QuestTripleLine = "quest.tripleLine";
}
