namespace FocusListener.Tests;

public sealed class AudioArbitrationBucketTests
{
    [Fact]
    public void Select_ConcatenatesEverySubBufferFromTheChosenRoute()
    {
        var bucket = new AudioArbitrationBucket(systemActivityThreshold: 0.006);
        for (var index = 0; index < 10; index++)
        {
            bucket.Add(new PcmAudioFrame(
                TimeBucket: 42,
                Route: ClassroomAudioRoute.Microphone,
                Pcm16: ConstantPcm(samples: 160, amplitude: 500),
                RootMeanSquare: 500 / 32768d));
            bucket.Add(new PcmAudioFrame(
                TimeBucket: 42,
                Route: ClassroomAudioRoute.SystemPlayback,
                Pcm16: ConstantPcm(samples: 160, amplitude: 2_000),
                RootMeanSquare: 2_000 / 32768d));
        }

        var selected = Assert.IsType<PcmAudioFrame>(bucket.Select());

        Assert.Equal(ClassroomAudioRoute.SystemPlayback, selected.Route);
        Assert.Equal(3_200, selected.Pcm16.Length);
        Assert.Equal(42, selected.TimeBucket);
        Assert.True(selected.RootMeanSquare >= 0.006);
    }

    private static byte[] ConstantPcm(int samples, short amplitude)
    {
        var bytes = new byte[samples * 2];
        for (var index = 0; index < bytes.Length; index += 2)
        {
            bytes[index] = (byte)(amplitude & 0xFF);
            bytes[index + 1] = (byte)(amplitude >> 8 & 0xFF);
        }

        return bytes;
    }
}
