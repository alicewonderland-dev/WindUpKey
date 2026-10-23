using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin.Services;

namespace WindUpKey.Services;

/// <summary>
/// User-editable doll RP chat lines (low-wind warnings and wind-received echoes).
/// Stored as JSON in the plugin config folder (<c>LowWindMessages.config</c>).
/// Missing/blank fields fall back to built-in defaults.
/// Reloads automatically when the file changes on disk.
/// </summary>
public sealed class LowWindMessagesConfig
{
    public const string FileName = "LowWindMessages.config";

    public const string DefaultHigh =
        "Your springs feel a little less taut - your winding is beginning to ebb.";

    public const string DefaultMid =
        "Your key is running low. Seek winding soon, before your steps grow stiff.";

    public const string DefaultLow =
        "Your winding is nearly spent. Find someone to re-wind you before you seize up.";

    public const string DefaultExpired =
        "Your winding has run out. Your springs seize - you can go no further without being rewound.";

    public const string DefaultWindLight =
        "Your key is given a light turn. Your springs catch a little more life.";

    public const string DefaultWindMedium =
        "Your key turns steadily in its lock. Winding works through your gears.";

    public const string DefaultWindDeep =
        "Your key turns long and deliberate. Springs fill; motion comes easier.";

    public const string DefaultWindFull =
        "Your key is wound thoroughly. The spring feels tense, and you feel full of energy.";

    public const string DefaultWindLightNamed =
        "{name} gives your key a light turn. Your springs catch a little more life.";

    public const string DefaultWindMediumNamed =
        "{name} turns your key steadily in its lock. Winding works through your gears.";

    public const string DefaultWindDeepNamed =
        "{name} gives your key several turns. Springs fill; motion comes easier.";

    public const string DefaultWindFullNamed =
        "{name} winds your key thoroughly. The spring feels tense, and you feel full of energy.";

    public const string DefaultUnwind =
        "Your key is pulled out. Your springs go slack before the key is reinserted - you can go no further without being rewound.";

    public const string DefaultUnwindNamed =
        "{name} pulls your key out. Your springs go slack before the key is reinserted - you can go no further without being rewound.";

    public const string DefaultCommendationWind =
        "On the way out, a grateful player gives your key a few welcome turns.";

    public const string DefaultBroken =
        "Something deep inside you snaps with a sharp twang - you've been wound far too tightly. You're broken, and can go no further until someone repairs you or your springs settle.";

    public const string DefaultBrokenNamed =
        "{name} gives your key one turn too many. Something deep inside you snaps with a sharp twang - you're broken, and can go no further until someone repairs you or your springs settle.";

    public const string DefaultRepaired =
        "Careful hands set your broken spring right. Your gears catch again.";

    public const string DefaultRepairedNamed =
        "{name} carefully sets your broken spring right. Your gears catch again.";

    public const string DefaultRecovered =
        "With a soft click, your strained springs settle back into place. You can move again.";

    public const string DefaultMendedUnwound =
        "Your broken spring settles back into place, but your key has no wind left behind it. You can go no further until you're rewound.";

    public const string DefaultMoodleFullyWoundTitle = "Fully Wound";
    public const string DefaultMoodleFullyWoundDescription =
        "Your key is wound tight. Springs hold strong; motion comes easily.";

    public const string DefaultMoodleWoundTitle = "Wound";
    public const string DefaultMoodleWoundDescription =
        "Your winding holds. Gears turn smoothly for now.";

    public const string DefaultMoodleLowTitle = "Winding Low";
    public const string DefaultMoodleLowDescription =
        "Your key is running low. Seek winding soon, before your steps grow stiff.";

    public const string DefaultMoodleNearlySpentTitle = "Nearly Spent";
    public const string DefaultMoodleNearlySpentDescription =
        "Your winding is nearly spent. Find someone to re-wind you before you seize up.";

    public const string DefaultMoodleUnwoundTitle = "Unwound";
    public const string DefaultMoodleUnwoundDescription =
        "Your winding has run out. Your springs seize - you can go no further without being rewound.";

    public const string DefaultMoodleOverwoundTitle = "Overwound";
    public const string DefaultMoodleOverwoundDescription =
        "Your springs are strained past their limit and creak with every step. Wind them any further and something may break.";

    public const string DefaultMoodleBrokenTitle = "Broken";
    public const string DefaultMoodleBrokenDescription =
        "Careless winding has snapped something inside. Your gears won't catch until you're repaired, or your springs settle.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly IPluginLog _log;
    private MessagesFile _messages = MessagesFile.CreateDefaults();
    private DateTime _loadedWriteUtc = DateTime.MinValue;

    public LowWindMessagesConfig(string configDirectory, IPluginLog log)
    {
        _path = System.IO.Path.Combine(configDirectory, FileName);
        _log = log;
        EnsureFileExists();
        ReloadIfNeeded(force: true);
    }

    public string FilePath => _path;

    public string High => Resolve(EnsureLoaded().High, DefaultHigh);
    public string Mid => Resolve(EnsureLoaded().Mid, DefaultMid);
    public string Low => Resolve(EnsureLoaded().Low, DefaultLow);
    public string Expired => Resolve(EnsureLoaded().Expired, DefaultExpired);
    public string WindLight => Resolve(EnsureLoaded().WindLight, DefaultWindLight);
    public string WindMedium => Resolve(EnsureLoaded().WindMedium, DefaultWindMedium);
    public string WindDeep => Resolve(EnsureLoaded().WindDeep, DefaultWindDeep);
    public string WindFull => Resolve(EnsureLoaded().WindFull, DefaultWindFull);
    public string WindLightNamed => Resolve(EnsureLoaded().WindLightNamed, DefaultWindLightNamed);
    public string WindMediumNamed => Resolve(EnsureLoaded().WindMediumNamed, DefaultWindMediumNamed);
    public string WindDeepNamed => Resolve(EnsureLoaded().WindDeepNamed, DefaultWindDeepNamed);
    public string WindFullNamed => Resolve(EnsureLoaded().WindFullNamed, DefaultWindFullNamed);
    public string Unwind => Resolve(EnsureLoaded().Unwind, DefaultUnwind);
    public string UnwindNamed => Resolve(EnsureLoaded().UnwindNamed, DefaultUnwindNamed);
    public string CommendationWind => Resolve(EnsureLoaded().CommendationWind, DefaultCommendationWind);

    public string MoodleFullyWoundTitle =>
        Resolve(EnsureLoaded().MoodleFullyWoundTitle, DefaultMoodleFullyWoundTitle);
    public string MoodleFullyWoundDescription =>
        Resolve(EnsureLoaded().MoodleFullyWoundDescription, DefaultMoodleFullyWoundDescription);
    public string MoodleWoundTitle =>
        Resolve(EnsureLoaded().MoodleWoundTitle, DefaultMoodleWoundTitle);
    public string MoodleWoundDescription =>
        Resolve(EnsureLoaded().MoodleWoundDescription, DefaultMoodleWoundDescription);
    public string MoodleLowTitle =>
        Resolve(EnsureLoaded().MoodleLowTitle, DefaultMoodleLowTitle);
    public string MoodleLowDescription =>
        Resolve(EnsureLoaded().MoodleLowDescription, DefaultMoodleLowDescription);
    public string MoodleNearlySpentTitle =>
        Resolve(EnsureLoaded().MoodleNearlySpentTitle, DefaultMoodleNearlySpentTitle);
    public string MoodleNearlySpentDescription =>
        Resolve(EnsureLoaded().MoodleNearlySpentDescription, DefaultMoodleNearlySpentDescription);
    public string MoodleUnwoundTitle =>
        Resolve(EnsureLoaded().MoodleUnwoundTitle, DefaultMoodleUnwoundTitle);
    public string MoodleUnwoundDescription =>
        Resolve(EnsureLoaded().MoodleUnwoundDescription, DefaultMoodleUnwoundDescription);
    public string MoodleOverwoundTitle =>
        Resolve(EnsureLoaded().MoodleOverwoundTitle, DefaultMoodleOverwoundTitle);
    public string MoodleOverwoundDescription =>
        Resolve(EnsureLoaded().MoodleOverwoundDescription, DefaultMoodleOverwoundDescription);
    public string MoodleBrokenTitle =>
        Resolve(EnsureLoaded().MoodleBrokenTitle, DefaultMoodleBrokenTitle);
    public string MoodleBrokenDescription =>
        Resolve(EnsureLoaded().MoodleBrokenDescription, DefaultMoodleBrokenDescription);
    public string Broken => Resolve(EnsureLoaded().Broken, DefaultBroken);
    public string BrokenNamed => Resolve(EnsureLoaded().BrokenNamed, DefaultBrokenNamed);
    public string Repaired => Resolve(EnsureLoaded().Repaired, DefaultRepaired);
    public string RepairedNamed => Resolve(EnsureLoaded().RepairedNamed, DefaultRepairedNamed);
    public string Recovered => Resolve(EnsureLoaded().Recovered, DefaultRecovered);
    public string MendedUnwound => Resolve(EnsureLoaded().MendedUnwound, DefaultMendedUnwound);

    private MessagesFile EnsureLoaded()
    {
        ReloadIfNeeded(force: false);
        return _messages;
    }

    private void EnsureFileExists()
    {
        if (File.Exists(_path))
            return;

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(MessagesFile.CreateDefaults(), JsonOptions));
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to create {Path}; using built-in low-wind messages", _path);
        }
    }

    private void ReloadIfNeeded(bool force)
    {
        try
        {
            if (!File.Exists(_path))
            {
                EnsureFileExists();
                if (!File.Exists(_path))
                    return;
            }

            var writeUtc = File.GetLastWriteTimeUtc(_path);
            if (!force && writeUtc == _loadedWriteUtc)
                return;

            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<MessagesFile>(json, JsonOptions);
            if (loaded is null)
            {
                _log.Warning("Low-wind messages file was empty or invalid; keeping previous messages");
                return;
            }

            _messages = loaded;
            _loadedWriteUtc = writeUtc;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to load {Path}; keeping previous/built-in low-wind messages", _path);
        }
    }

    private static string Resolve(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private sealed class MessagesFile
    {
        /// <summary>Ignored by the plugin; explains the file for editors.</summary>
        public string? Comment { get; set; }

        public string? High { get; set; }
        public string? Mid { get; set; }
        public string? Low { get; set; }
        public string? Expired { get; set; }
        public string? WindLight { get; set; }
        public string? WindMedium { get; set; }
        public string? WindDeep { get; set; }
        public string? WindFull { get; set; }
        public string? WindLightNamed { get; set; }
        public string? WindMediumNamed { get; set; }
        public string? WindDeepNamed { get; set; }
        public string? WindFullNamed { get; set; }
        public string? Unwind { get; set; }
        public string? UnwindNamed { get; set; }
        public string? CommendationWind { get; set; }

        public string? MoodleFullyWoundTitle { get; set; }
        public string? MoodleFullyWoundDescription { get; set; }
        public string? MoodleWoundTitle { get; set; }
        public string? MoodleWoundDescription { get; set; }
        public string? MoodleLowTitle { get; set; }
        public string? MoodleLowDescription { get; set; }
        public string? MoodleNearlySpentTitle { get; set; }
        public string? MoodleNearlySpentDescription { get; set; }
        public string? MoodleUnwoundTitle { get; set; }
        public string? MoodleUnwoundDescription { get; set; }
        public string? MoodleOverwoundTitle { get; set; }
        public string? MoodleOverwoundDescription { get; set; }
        public string? MoodleBrokenTitle { get; set; }
        public string? MoodleBrokenDescription { get; set; }
        public string? Broken { get; set; }
        public string? BrokenNamed { get; set; }
        public string? Repaired { get; set; }
        public string? RepairedNamed { get; set; }
        public string? Recovered { get; set; }
        public string? MendedUnwound { get; set; }

        public static MessagesFile CreateDefaults() => new()
        {
            Comment =
                "Doll RP chat lines and Moodle titles/descriptions. Color tags (chat only): <c:pink>text</c> " +
                "(also red, orange, yellow, green, blue, purple, grey, white). " +
                "Untagged chat text uses the default chat color; only [Wind-Up Key] is pink by default. " +
                "low-wind: high/mid/low/expired. " +
                "wind-received: windLight/Medium/Deep/Full (+ *Named with {name}). " +
                "unwind / unwindNamed: partner removed your key. " +
                "commendationWind: a completed-duty commendation winds the doll. " +
                "broken / brokenNamed: wound too far by others; repaired / repairedNamed / recovered: the break ends. " +
                "moodle*: titles/descriptions for wind-charge Moodles (no exact time). Blank = built-in default.",
            High = DefaultHigh,
            Mid = DefaultMid,
            Low = DefaultLow,
            Expired = DefaultExpired,
            WindLight = DefaultWindLight,
            WindMedium = DefaultWindMedium,
            WindDeep = DefaultWindDeep,
            WindFull = DefaultWindFull,
            WindLightNamed = DefaultWindLightNamed,
            WindMediumNamed = DefaultWindMediumNamed,
            WindDeepNamed = DefaultWindDeepNamed,
            WindFullNamed = DefaultWindFullNamed,
            Unwind = DefaultUnwind,
            UnwindNamed = DefaultUnwindNamed,
            CommendationWind = DefaultCommendationWind,
            MoodleFullyWoundTitle = DefaultMoodleFullyWoundTitle,
            MoodleFullyWoundDescription = DefaultMoodleFullyWoundDescription,
            MoodleWoundTitle = DefaultMoodleWoundTitle,
            MoodleWoundDescription = DefaultMoodleWoundDescription,
            MoodleLowTitle = DefaultMoodleLowTitle,
            MoodleLowDescription = DefaultMoodleLowDescription,
            MoodleNearlySpentTitle = DefaultMoodleNearlySpentTitle,
            MoodleNearlySpentDescription = DefaultMoodleNearlySpentDescription,
            MoodleUnwoundTitle = DefaultMoodleUnwoundTitle,
            MoodleUnwoundDescription = DefaultMoodleUnwoundDescription,
            MoodleOverwoundTitle = DefaultMoodleOverwoundTitle,
            MoodleOverwoundDescription = DefaultMoodleOverwoundDescription,
            MoodleBrokenTitle = DefaultMoodleBrokenTitle,
            MoodleBrokenDescription = DefaultMoodleBrokenDescription,
            Broken = DefaultBroken,
            BrokenNamed = DefaultBrokenNamed,
            Repaired = DefaultRepaired,
            RepairedNamed = DefaultRepairedNamed,
            Recovered = DefaultRecovered,
            MendedUnwound = DefaultMendedUnwound,
        };
    }
}
