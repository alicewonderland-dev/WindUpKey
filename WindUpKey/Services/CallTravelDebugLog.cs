using System;
using System.IO;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace WindUpKey.Services;

/// <summary>
/// Append-only Call travel diagnostics when <see cref="Configuration.IsDebugEnabled"/>.
/// Written by both the Call sender (owner snapshot) and the doll travel state machine.
/// </summary>
internal static class CallTravelDebugLog
{
    public const string FileName = "CallTravel.debug.log";

    public static void Write(
        IDalamudPluginInterface pi,
        Configuration config,
        IPluginLog log,
        string message,
        bool force = false)
    {
        if (!force && !config.IsDebugEnabled)
            return;

        log.Debug("[CallTravel] {Message}", message);

        try
        {
            var dir = pi.GetPluginConfigDirectory();
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, FileName);
            var line = $"{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z {message}{Environment.NewLine}";
            File.AppendAllText(path, line);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Call travel debug file write failed");
        }
    }
}
