using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;

namespace WindUpKey.Services;

/// <summary>
/// Plays bundled WAV sound effects for wind / expiry.
/// Wind-up clip length scales with hours added (same bands as low-wind RP chat).
/// </summary>
public sealed class SoundEffectService : IDisposable
{
    private const string WindUpFileName = "windingup.wav";
    private const string WindDownFileName = "windingdown.wav";

    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndMemory = 0x0004;
    private const uint SndPurge = 0x0040;

    private readonly Configuration _config;
    private readonly IPluginLog _log;
    private readonly string _soundsDir;
    private readonly object _gate = new();

    private byte[]? _windUpWav;
    private byte[]? _windDownWav;
    private bool _windUpLoadAttempted;
    private bool _windDownLoadAttempted;
    private bool _windUpWarned;
    private bool _windDownWarned;
    /// <summary>06-A6: the buffers PlaySound may still be reading, each kept until its clip's expected end.</summary>
    private readonly ClipLedger<GCHandle> _pins = new();
    /// <summary>06-A12: the ledger's clock. Monotonic: a wall-clock jump can't free a buffer PlaySound may still read.</summary>
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _disposed;

    public SoundEffectService(Configuration config, IPluginLog log, string soundsDirectory)
    {
        _config = config;
        _log = log;
        _soundsDir = soundsDirectory;
    }

    /// <summary>Play a prefix of windingup.wav based on hours added.</summary>
    public void PlayWind(TimeSpan hoursAdded)
    {
        if (!_config.SoundEffectsEnabled || hoursAdded <= TimeSpan.Zero)
            return;

        var maxSeconds = MaxSecondsForHours(hoursAdded.TotalHours);
        PlayCached(ref _windUpWav, ref _windUpLoadAttempted, ref _windUpWarned, WindUpFileName, maxSeconds);
    }

    /// <summary>Natural timer expiry — full windingdown.wav.</summary>
    public void PlayExpire()
    {
        if (!_config.SoundEffectsEnabled)
            return;

        PlayCached(ref _windDownWav, ref _windDownLoadAttempted, ref _windDownWarned, WindDownFileName, maxSeconds: null);
    }

    /// <summary>
    /// Partner/debug unwind. Currently same clip as <see cref="PlayExpire"/>;
    /// can later point at a dedicated file without changing call sites.
    /// </summary>
    public void PlayUnwind() => PlayExpire();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        lock (_gate)
        {
            // 06-A6: stop only if one of our clips might still be playing (its buffer is about to be freed); otherwise
            // leave whatever else in the game is using PlaySound alone.
            if (_pins.MightBePlaying(_clock.Elapsed))
                StopLocked();
            Free(_pins.TakeAll());
        }
    }

    /// <summary>Same hour bands as <see cref="LowWindWarningService.OnWoundReceived"/>.</summary>
    internal static double? MaxSecondsForHours(double hours) =>
        hours < 3 ? 1
        : hours < 9 ? 3
        : hours < 18 ? 5
        : null;

    private void PlayCached(
        ref byte[]? cache,
        ref bool loadAttempted,
        ref bool warned,
        string fileName,
        double? maxSeconds)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            if (!loadAttempted)
            {
                loadAttempted = true;
                cache = TryLoadWav(fileName, ref warned);
            }

            if (cache is null || cache.Length == 0)
                return;

            byte[] toPlay;
            try
            {
                toPlay = maxSeconds is { } seconds
                    ? TruncateWav(cache, seconds)
                    : cache;
            }
            catch (Exception ex)
            {
                if (!warned)
                {
                    warned = true;
                    _log.Warning(ex, "WindUpKey failed to prepare sound {File}", fileName);
                }

                return;
            }

            // 06-A6: no SND_PURGE first (it stopped every PlaySound sound in the game, other plugins' too). A new
            // PlaySound replaces our previous clip by itself; the old buffer stays pinned until that clip's expected end.
            var pin = GCHandle.Alloc(toPlay, GCHandleType.Pinned);
            if (!PlaySound(pin.AddrOfPinnedObject(), IntPtr.Zero, SndAsync | SndMemory | SndNoDefault))
            {
                pin.Free();
                Free(_pins.Expire(_clock.Elapsed));
                if (!warned)
                {
                    warned = true;
                    _log.Warning("WindUpKey PlaySound failed for {File}", fileName);
                }

                return;
            }

            Free(_pins.Add(pin, WavHeader.Duration(toPlay), _clock.Elapsed));
        }
    }

    private byte[]? TryLoadWav(string fileName, ref bool warned)
    {
        var path = Path.Combine(_soundsDir, fileName);
        try
        {
            if (!File.Exists(path))
            {
                if (!warned)
                {
                    warned = true;
                    _log.Warning("WindUpKey sound file missing: {Path}", path);
                }

                return null;
            }

            return File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            if (!warned)
            {
                warned = true;
                _log.Warning(ex, "WindUpKey failed to load sound {Path}", path);
            }

            return null;
        }
    }

    /// <summary>Stops the process's PlaySound audio. 06-A6: only from <see cref="Dispose"/>, while our clip might be playing.</summary>
    private static void StopLocked() =>
        PlaySound(IntPtr.Zero, IntPtr.Zero, SndPurge);

    private static void Free(IReadOnlyList<GCHandle> pins)
    {
        foreach (var pin in pins)
        {
            if (pin.IsAllocated)
                pin.Free();
        }
    }

    /// <summary>Build a new PCM WAV containing at most <paramref name="maxSeconds"/> of audio.</summary>
    internal static byte[] TruncateWav(byte[] wav, double maxSeconds)
    {
        if (maxSeconds <= 0)
            return wav;

        // 06-A13: the chunks are read by the same parser as the clip length (WavHeader).
        if (!WavHeader.TryRead(wav, out var layout))
            throw new InvalidDataException("Invalid WAV.");

        var channels = layout.Channels;
        var bitsPerSample = layout.BitsPerSample;
        var dataOffset = layout.DataOffset;
        var dataLength = layout.DataLength;
        if (channels == 0 || layout.SampleRate == 0 || bitsPerSample == 0)
            throw new InvalidDataException("WAV missing fmt/data.");

        var bytesPerSecond = layout.SampleRate * channels * (bitsPerSample / 8u);
        if (bytesPerSecond == 0)
            throw new InvalidDataException("Invalid WAV format.");

        var maxBytes = (int)Math.Min(dataLength, Math.Floor(maxSeconds * bytesPerSecond));
        // Align to sample frame.
        var frame = channels * (bitsPerSample / 8);
        if (frame > 0)
            maxBytes -= maxBytes % frame;

        if (maxBytes >= dataLength)
            return wav;

        // Minimal PCM WAV: RIFF + fmt + data (copy fmt from source).
        var fmtChunkOffset = layout.FmtChunkOffset;
        var fmtPayload = layout.FmtSize;
        var outLen = 12 + 8 + fmtPayload + 8 + maxBytes;
        var result = new byte[outLen];
        // RIFF header
        result[0] = (byte)'R';
        result[1] = (byte)'I';
        result[2] = (byte)'F';
        result[3] = (byte)'F';
        BitConverter.TryWriteBytes(result.AsSpan(4, 4), outLen - 8);
        result[8] = (byte)'W';
        result[9] = (byte)'A';
        result[10] = (byte)'V';
        result[11] = (byte)'E';
        // fmt
        Buffer.BlockCopy(wav, fmtChunkOffset, result, 12, 8 + fmtPayload);
        var dataHeader = 12 + 8 + fmtPayload;
        result[dataHeader] = (byte)'d';
        result[dataHeader + 1] = (byte)'a';
        result[dataHeader + 2] = (byte)'t';
        result[dataHeader + 3] = (byte)'a';
        BitConverter.TryWriteBytes(result.AsSpan(dataHeader + 4, 4), maxBytes);
        Buffer.BlockCopy(wav, dataOffset, result, dataHeader + 8, maxBytes);
        return result;
    }

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", SetLastError = true)]
    private static extern bool PlaySound(IntPtr pszSound, IntPtr hmod, uint fdwSound);
}
