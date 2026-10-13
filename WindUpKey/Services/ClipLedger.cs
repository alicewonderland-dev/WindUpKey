using System;
using System.Collections.Generic;

namespace WindUpKey.Services;

/// <summary>
/// 06-A6: which pinned WAV buffers <c>PlaySound(SND_MEMORY | SND_ASYNC)</c> may still be reading. The sound service no
/// longer stops all of the game's PlaySound audio (<c>SND_PURGE</c>) before each clip, so a buffer is kept pinned until
/// its clip's expected end (from its WAV header) plus <see cref="Margin"/>. At most <see cref="MaxPins"/> are kept:
/// PlaySound plays one sound per process, so a clip two plays back has been replaced and its buffer can go. No Dalamud
/// dependency; <typeparamref name="T"/> is the pin (a <c>GCHandle</c> in the plugin).
/// <para>
/// 06-A12: times are on a monotonic clock (the caller's <c>Stopwatch.Elapsed</c>), never wall-clock time, so a clock
/// jump can't free a buffer while PlaySound may still be reading it.
/// </para>
/// </summary>
public sealed class ClipLedger<T>
{
    /// <summary>Kept past a clip's expected end, for the audio device's own latency.</summary>
    public static readonly TimeSpan Margin = TimeSpan.FromSeconds(2);

    /// <summary>The playing clip and the one it replaced.</summary>
    public const int MaxPins = 2;

    /// <summary>A clip whose header can't be read is kept this long (it most likely didn't play at all).</summary>
    public static readonly TimeSpan UnknownDuration = TimeSpan.FromSeconds(30);

    private readonly List<(T Pin, TimeSpan End)> _pins = [];

    /// <summary>How many pins are kept.</summary>
    public int Count => _pins.Count;

    /// <summary>
    /// Records <paramref name="pin"/>, whose clip of <paramref name="duration"/> (null when unknown) started at
    /// <paramref name="now"/> (monotonic). Returns the pins the caller frees now: those past their clip's end plus the margin,
    /// and the oldest beyond <see cref="MaxPins"/>.
    /// </summary>
    public IReadOnlyList<T> Add(T pin, TimeSpan? duration, TimeSpan now)
    {
        var toFree = Expire(now);
        var length = duration is { } d && d > TimeSpan.Zero ? d : UnknownDuration;
        _pins.Add((pin, now + length + Margin));
        while (_pins.Count > MaxPins)
        {
            toFree.Add(_pins[0].Pin);
            _pins.RemoveAt(0);
        }

        return toFree;
    }

    /// <summary>Removes and returns the pins whose clip ended (plus the margin) by <paramref name="now"/> (monotonic).</summary>
    public List<T> Expire(TimeSpan now)
    {
        var expired = new List<T>();
        _pins.RemoveAll(p =>
        {
            if (p.End > now)
                return false;
            expired.Add(p.Pin);
            return true;
        });
        return expired;
    }

    /// <summary>
    /// Whether one of our clips might still be playing at <paramref name="now"/> (monotonic): only then does unloading stop the
    /// process's PlaySound audio before freeing its buffer.
    /// </summary>
    public bool MightBePlaying(TimeSpan now)
    {
        foreach (var (_, end) in _pins)
        {
            if (end > now)
                return true;
        }

        return false;
    }

    /// <summary>Removes and returns every pin (unload).</summary>
    public List<T> TakeAll()
    {
        var all = new List<T>(_pins.Count);
        foreach (var (pin, _) in _pins)
            all.Add(pin);
        _pins.Clear();
        return all;
    }
}

/// <summary>
/// 06-A13: where a WAV's fmt and data chunks are, and the fmt fields the plugin uses (<see cref="WavHeader.TryRead"/>).
/// </summary>
/// <param name="FmtChunkOffset">The fmt chunk's header (its id), which <c>SoundEffectService.TruncateWav</c> copies.</param>
/// <param name="FmtSize">The fmt chunk's payload size.</param>
/// <param name="Format">06-A15: the fmt chunk's format tag (1 for PCM).</param>
/// <param name="ByteRate">The fmt chunk's average bytes per second (at +8), right for compressed formats too.</param>
/// <param name="BlockAlign">06-A15: the fmt chunk's bytes per sample frame (at +12).</param>
/// <param name="DataOffset">The data chunk's first audio byte.</param>
/// <param name="DataLength">The data chunk's payload size.</param>
public readonly record struct WavLayout(
    int FmtChunkOffset,
    int FmtSize,
    ushort Format,
    ushort Channels,
    uint SampleRate,
    uint ByteRate,
    ushort BlockAlign,
    ushort BitsPerSample,
    int DataOffset,
    int DataLength);

/// <summary>
/// 06-A6: reads a WAV's play length from its header. 06-A13: from the fmt chunk's byte rate, so any format (not only
/// PCM) gets a real length; the chunk parsing is shared with <c>SoundEffectService.TruncateWav</c>. No Dalamud dependency.
/// </summary>
public static class WavHeader
{
    private const uint FmtChunkId = 0x20746D66; // "fmt "
    private const uint DataChunkId = 0x61746164; // "data"
    private const ushort PcmFormat = 1;

    /// <summary>
    /// The length of <paramref name="wav"/>'s audio (its data chunk over the fmt byte rate), or null when unreadable.
    /// 06-A15: for PCM, over the smaller of the byte rate and sample rate x block align (when that is above zero), so a
    /// wrong byte rate in a user-replaced file gives the longer length and its buffer isn't freed while PlaySound still
    /// reads it.
    /// </summary>
    public static TimeSpan? Duration(byte[] wav)
    {
        if (!TryRead(wav, out var layout))
            return null;

        var rate = (ulong)layout.ByteRate;
        // 06-A15: a zero sample rate or block align leaves the byte rate alone, so PCM is never shorter than by it.
        var frameRate = (ulong)layout.SampleRate * layout.BlockAlign;
        if (layout.Format == PcmFormat && frameRate > 0)
            rate = Math.Min(rate, frameRate);
        if (rate == 0)
            return null;

        return TimeSpan.FromSeconds(layout.DataLength / (double)rate);
    }

    /// <summary>
    /// Reads the RIFF chunks up to the first data chunk. False when <paramref name="wav"/> isn't a RIFF file, a chunk runs
    /// past its end, the fmt chunk is shorter than 16 bytes, or no fmt chunk comes before the data chunk.
    /// </summary>
    public static bool TryRead(byte[] wav, out WavLayout layout)
    {
        layout = default;
        if (wav.Length < 44 || wav[0] != (byte)'R' || wav[1] != (byte)'I' || wav[2] != (byte)'F' || wav[3] != (byte)'F')
            return false;

        var offset = 12;
        var fmtChunkOffset = -1;
        var fmtSize = 0;
        ushort format = 0;
        ushort channels = 0;
        uint sampleRate = 0;
        uint byteRate = 0;
        ushort blockAlign = 0;
        ushort bitsPerSample = 0;
        while (offset + 8 <= wav.Length)
        {
            var chunkId = BitConverter.ToUInt32(wav, offset);
            var chunkSize = BitConverter.ToInt32(wav, offset + 4);
            var chunkData = offset + 8;
            if (chunkSize < 0 || chunkData + (long)chunkSize > wav.Length)
                return false;

            if (chunkId == FmtChunkId)
            {
                if (chunkSize < 16)
                    return false;
                fmtChunkOffset = offset;
                fmtSize = chunkSize;
                format = BitConverter.ToUInt16(wav, chunkData);
                channels = BitConverter.ToUInt16(wav, chunkData + 2);
                sampleRate = BitConverter.ToUInt32(wav, chunkData + 4);
                byteRate = BitConverter.ToUInt32(wav, chunkData + 8);
                blockAlign = BitConverter.ToUInt16(wav, chunkData + 12);
                bitsPerSample = BitConverter.ToUInt16(wav, chunkData + 14);
            }
            else if (chunkId == DataChunkId)
            {
                if (fmtChunkOffset < 0)
                    return false;
                layout = new WavLayout(
                    fmtChunkOffset, fmtSize, format, channels, sampleRate, byteRate, blockAlign, bitsPerSample, chunkData, chunkSize);
                return true;
            }

            offset = chunkData + chunkSize + (chunkSize & 1);
        }

        return false;
    }
}
