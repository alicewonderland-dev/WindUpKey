using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin.Services;

namespace WindUpKey.Services;

/// <summary>
/// User-editable low-wind RP chat lines. Stored as JSON in the plugin config folder
/// (<c>LowWindMessages.config</c>). Missing/blank fields fall back to built-in defaults.
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

        public static MessagesFile CreateDefaults() => new()
        {
            Comment =
                "Low-wind RP chat lines for dolls. Edit and save; changes apply on the next warning. " +
                "Blank fields use the built-in default.",
            High = DefaultHigh,
            Mid = DefaultMid,
            Low = DefaultLow,
            Expired = DefaultExpired,
        };
    }
}
