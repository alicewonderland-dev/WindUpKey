#if WINDUP_TESTING
using System;
using System.IO;
using System.Text;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace WindUpKey.Services;

/// <summary>
/// Call travel diagnostics, appended to <see cref="FileName"/> in the config folder. Written by both the Call sender
/// (owner snapshot) and the doll travel state machine. Routine lines need <see cref="Configuration.IsDebugEnabled"/>;
/// a forced line is written regardless and must be location-free (07-A8, <see cref="CallLogRules"/>). The file rotates
/// at <see cref="CallLogRules.RotateAtBytes"/>, keeping one older file (D11). Testing builds only.
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
            var existing = new FileInfo(path);
            if (existing.Exists && CallLogRules.ShouldRotate(existing.Length, Encoding.UTF8.GetByteCount(line)))
                File.Move(path, path + CallLogRules.RotatedSuffix, overwrite: true);
            File.AppendAllText(path, line);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Call travel debug file write failed");
        }
    }
}
#endif
