using BgRecorder.Audio.Mixing;

namespace BgRecorder.Audio.Tests;

public class PcmMathTests
{
    [Fact]
    public void MixSample_SumsAtUnityGain()
    {
        Assert.Equal(3000, PcmMath.MixSample(1000, 2000));
        Assert.Equal(-3000, PcmMath.MixSample(-1000, -2000));
        Assert.Equal(0, PcmMath.MixSample(1234, -1234));
    }

    [Fact]
    public void MixSample_ClipsAtPositiveCeiling()
    {
        // 20000 + 20000 = 40000 would overflow Int16; must clamp to 32767, not wrap to negative.
        Assert.Equal(short.MaxValue, PcmMath.MixSample(20000, 20000));
        Assert.Equal(short.MaxValue, PcmMath.MixSample(short.MaxValue, short.MaxValue));
    }

    [Fact]
    public void MixSample_ClipsAtNegativeFloor()
    {
        Assert.Equal(short.MinValue, PcmMath.MixSample(-20000, -20000));
        Assert.Equal(short.MinValue, PcmMath.MixSample(short.MinValue, short.MinValue));
    }

    [Theory]
    [InlineData(40000, 32767)]
    [InlineData(-40000, -32768)]
    [InlineData(100, 100)]
    [InlineData(32767, 32767)]
    [InlineData(-32768, -32768)]
    public void ClampToInt16_BoundsCorrectly(int input, int expected)
        => Assert.Equal((short)expected, PcmMath.ClampToInt16(input));

    [Fact]
    public void MixInterleaved_HandlesUnequalLengthsAsSilence()
    {
        short[] game = [10000, 10000, 10000, 10000];
        short[] mic = [5000, 5000]; // shorter: remaining frames mix against silence
        var dest = new short[4];

        PcmMath.MixInterleaved(game, mic, dest);

        Assert.Equal(15000, dest[0]);
        Assert.Equal(15000, dest[1]);
        Assert.Equal(10000, dest[2]);
        Assert.Equal(10000, dest[3]);
    }

    [Fact]
    public void SilenceFrameCount_ConvertsDurationToFrames()
    {
        Assert.Equal(44100, PcmMath.SilenceFrameCount(44100, TimeSpan.FromSeconds(1)));
        Assert.Equal(66150, PcmMath.SilenceFrameCount(44100, TimeSpan.FromSeconds(1.5)));
        Assert.Equal(22050, PcmMath.SilenceFrameCount(44100, TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void SilenceFrameCount_ClampsNonPositiveOffsetsToZero()
    {
        Assert.Equal(0, PcmMath.SilenceFrameCount(44100, TimeSpan.Zero));
        Assert.Equal(0, PcmMath.SilenceFrameCount(44100, TimeSpan.FromSeconds(-2)));
    }

    [Fact]
    public void SilenceByteCount_AccountsForChannelsAndBitDepth()
    {
        // 1.5 s * 44100 frames * 2 ch * 2 bytes = 66150 * 4 = 264600 bytes.
        Assert.Equal(264600, PcmMath.SilenceByteCount(44100, 2, 16, TimeSpan.FromSeconds(1.5)));
    }
}
