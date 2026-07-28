using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Input;

namespace WindUpKey.Services;

/// <summary>
/// Privacy-safe native-input diagnostics for public Testing builds.
/// Never records character identity, pairing data, destinations, or relay configuration.
/// </summary>
public sealed class InputDiagnosticLog : IDisposable
{
    public const string FileName = "WindUpKey.diagnostic.log";
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);

    private readonly object _sync = new();
    private readonly IPluginLog _pluginLog;
    private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);
    private string? _path;
    private DateTimeOffset _nextFlushUtc;
    private bool _disposed;

    public InputDiagnosticLog(
        IDalamudPluginInterface pluginInterface,
        IPluginLog pluginLog,
        string pluginVersion,
        bool enabled)
    {
        _pluginLog = pluginLog;
        if (!enabled)
            return;

        try
        {
            var directory = pluginInterface.GetPluginConfigDirectory();
            Directory.CreateDirectory(directory);
            _path = Path.Combine(directory, FileName);
            var previousPath = _path + ".previous.log";
            if (File.Exists(_path))
                File.Move(_path, previousPath, overwrite: true);

            _nextFlushUtc = DateTimeOffset.UtcNow + FlushInterval;
            WriteLine("session-start");
            WriteLine($"plugin-version={Sanitize(pluginVersion)}");
            WriteLine($"runtime={Sanitize(RuntimeInformation.FrameworkDescription)} os={Sanitize(RuntimeInformation.OSDescription)} arch={RuntimeInformation.ProcessArchitecture}");
            WriteLine($"dalamud-assembly={Sanitize(typeof(IDalamudPlugin).Assembly.GetName().Version?.ToString() ?? "unknown")}");
            WriteLine($"clientstructs-assembly={Sanitize(typeof(InputData).Assembly.GetName().Version?.ToString() ?? "unknown")}");
            WriteGameExecutableIdentity();
            WriteLine($"diagnostic-path={FileName}");
        }
        catch (Exception ex)
        {
            _path = null;
            _pluginLog.Warning(ex, "WindUpKey diagnostic file initialization failed");
        }
    }

    public bool Enabled => _path is not null && !_disposed;

    public void RecordState(string message)
    {
        if (Enabled)
            WriteLine($"state {Sanitize(message)}");
    }

    public void RecordHook(string hook, bool installed, Exception? exception = null)
    {
        if (!Enabled)
            return;

        var result = installed ? "installed" : "failed";
        var detail = exception is null
            ? string.Empty
            : $" exception={Sanitize(exception.GetType().Name)} message={Sanitize(exception.Message)}";
        WriteLine($"hook name={Sanitize(hook)} result={result}{detail}");
    }

    public void Count(string name, int amount = 1)
    {
        if (!Enabled)
            return;

        lock (_sync)
        {
            _counters.TryGetValue(name, out var current);
            _counters[name] = current + amount;
        }
    }

    public void RecordInputResult(string query, InputId inputId, bool result, bool blocked)
    {
        if (!Enabled || (!result && !blocked))
            return;

        Count($"input.{query}.{inputId}.{(blocked ? "blocked" : "true")}");
    }

    public void Tick()
    {
        if (!Enabled || DateTimeOffset.UtcNow < _nextFlushUtc)
            return;

        Dictionary<string, int> snapshot;
        lock (_sync)
        {
            snapshot = new Dictionary<string, int>(_counters, StringComparer.Ordinal);
            _counters.Clear();
            _nextFlushUtc = DateTimeOffset.UtcNow + FlushInterval;
        }

        if (snapshot.Count == 0)
            return;

        var entries = new List<string>(snapshot.Count);
        foreach (var pair in snapshot)
            entries.Add($"{Sanitize(pair.Key)}={pair.Value}");
        entries.Sort(StringComparer.Ordinal);
        WriteLine($"activity {string.Join(' ', entries)}");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Tick();
        WriteLine("session-end");
        _disposed = true;
    }

    private void WriteGameExecutableIdentity()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
            {
                WriteLine("game-executable=unavailable");
                return;
            }

            var info = FileVersionInfo.GetVersionInfo(processPath);
            using var stream = File.OpenRead(processPath);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            WriteLine(
                $"game-executable file={Sanitize(Path.GetFileName(processPath))} size={stream.Length} " +
                $"file-version={Sanitize(info.FileVersion ?? "unknown")} product-version={Sanitize(info.ProductVersion ?? "unknown")} sha256={hash}");
        }
        catch (Exception ex)
        {
            WriteLine($"game-executable=error exception={Sanitize(ex.GetType().Name)} message={Sanitize(ex.Message)}");
        }
    }

    private void WriteLine(string message)
    {
        var path = _path;
        if (path is null)
            return;

        try
        {
            var line = $"{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z {message}{Environment.NewLine}";
            lock (_sync)
                File.AppendAllText(path, line);
        }
        catch (Exception ex)
        {
            _pluginLog.Debug(ex, "WindUpKey diagnostic file write failed");
        }
    }

    private static string Sanitize(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
}
