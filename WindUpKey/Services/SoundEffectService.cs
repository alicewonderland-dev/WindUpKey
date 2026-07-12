using System;
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
    private GCHandle _pinnedPlaying;
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
            StopLocked();
            FreePinLocked();
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

            StopLocked();
            FreePinLocked();

            _pinnedPlaying = GCHandle.Alloc(toPlay, GCHandleType.Pinned);
            if (!PlaySound(_pinnedPlaying.AddrOfPinnedObject(), IntPtr.Zero, SndAsync | SndMemory | SndNoDefault))
            {
                FreePinLocked();
                if (!warned)
                {
                    warned = true;
                    _log.Warning("WindUpKey PlaySound failed for {File}", fileName);
                }
            }
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

    private void StopLocked() =>
        PlaySound(IntPtr.Zero, IntPtr.Zero, SndPurge);

    private void FreePinLocked()
    {
        if (_pinnedPlaying.IsAllocated)
            _pinnedPlaying.Free();
    }

    /// <summary>Build a new PCM WAV containing at most <paramref name="maxSeconds"/> of audio.</summary>
    internal static byte[] TruncateWav(byte[] wav, double maxSeconds)
    {
        if (maxSeconds <= 0)
            return wav;

        if (wav.Length < 44 || wav[0] != (byte)'R' || wav[1] != (byte)'I' || wav[2] != (byte)'F' || wav[3] != (byte)'F')
            throw new InvalidDataException("Not a RIFF file.");

        var offset = 12;
        ushort channels = 0;
        uint sampleRate = 0;
        ushort bitsPerSample = 0;
        var dataOffset = -1;
        var dataLength = 0;

        while (offset + 8 <= wav.Length)
        {
            var chunkId = BitConverter.ToUInt32(wav, offset);
            var chunkSize = BitConverter.ToInt32(wav, offset + 4);
            var chunkData = offset + 8;
            if (chunkSize < 0 || chunkData + chunkSize > wav.Length)
                throw new InvalidDataException("Invalid WAV chunk.");

            // "fmt "
            if (chunkId == 0x20746D66)
            {
                if (chunkSize < 16)
                    throw new InvalidDataException("Invalid fmt chunk.");
                channels = BitConverter.ToUInt16(wav, chunkData + 2);
                sampleRate = BitConverter.ToUInt32(wav, chunkData + 4);
                bitsPerSample = BitConverter.ToUInt16(wav, chunkData + 14);
            }
            // "data"
            else if (chunkId == 0x61746164)
            {
                dataOffset = chunkData;
                dataLength = chunkSize;
                break;
            }

            offset = chunkData + chunkSize + (chunkSize & 1);
        }

        if (dataOffset < 0 || channels == 0 || sampleRate == 0 || bitsPerSample == 0)
            throw new InvalidDataException("WAV missing fmt/data.");

        var bytesPerSecond = sampleRate * channels * (bitsPerSample / 8u);
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
        var fmtChunkOffset = 12;
        int fmtSize;
        while (true)
        {
            var id = BitConverter.ToUInt32(wav, fmtChunkOffset);
            var size = BitConverter.ToInt32(wav, fmtChunkOffset + 4);
            if (id == 0x20746D66)
            {
                fmtSize = size;
                break;
            }

            fmtChunkOffset += 8 + size + (size & 1);
        }

        var fmtPayload = fmtSize;
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
