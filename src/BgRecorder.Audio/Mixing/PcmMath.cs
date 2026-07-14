namespace BgRecorder.Audio.Mixing;

/// <summary>
/// Pure, allocation-free primitives for 16-bit PCM mixing and offset math.
/// Kept separate from the NAudio plumbing so the sum/clip/silence rules are
/// unit-testable in isolation.
/// </summary>
public static class PcmMath
{
    /// <summary>
    /// Sum two 16-bit samples at unity gain with a hard clipping guard so the mix
    /// never wraps around at the top or bottom of the 16-bit range.
    /// </summary>
    public static short MixSample(short a, short b) => ClampToInt16(a + b);

    /// <summary>Clamp an integer sum into the signed 16-bit range [-32768, 32767].</summary>
    public static short ClampToInt16(int value)
    {
        if (value > short.MaxValue) return short.MaxValue;
        if (value < short.MinValue) return short.MinValue;
        return (short)value;
    }

    /// <summary>
    /// Mix two interleaved 16-bit PCM blocks of equal length into <paramref name="destination"/>.
    /// Each element is one sample (channels are already interleaved by the caller).
    /// </summary>
    public static void MixInterleaved(ReadOnlySpan<short> game, ReadOnlySpan<short> mic, Span<short> destination)
    {
        int n = destination.Length;
        for (int i = 0; i < n; i++)
        {
            short g = i < game.Length ? game[i] : (short)0;
            short m = i < mic.Length ? mic[i] : (short)0;
            destination[i] = MixSample(g, m);
        }
    }

    /// <summary>
    /// Number of whole audio frames of leading silence needed to shift audio by
    /// <paramref name="offset"/> at the given sample rate. Negative offsets clamp to 0
    /// (a negative offset trims leading audio instead of inserting silence).
    /// </summary>
    public static long SilenceFrameCount(int sampleRate, TimeSpan offset)
    {
        if (offset <= TimeSpan.Zero)
            return 0;
        return (long)Math.Round(offset.TotalSeconds * sampleRate, MidpointRounding.AwayFromZero);
    }

    /// <summary>Byte length of a block of interleaved silence for the given frame count.</summary>
    public static long SilenceByteCount(int sampleRate, int channels, int bitsPerSample, TimeSpan offset)
        => SilenceFrameCount(sampleRate, offset) * channels * (bitsPerSample / 8);
}
