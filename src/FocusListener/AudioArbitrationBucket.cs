namespace FocusListener;

internal sealed class AudioArbitrationBucket(double systemActivityThreshold)
{
    private readonly List<PcmAudioFrame> _microphone = [];
    private readonly List<PcmAudioFrame> _systemPlayback = [];

    public void Add(PcmAudioFrame frame)
    {
        if (frame.Route == ClassroomAudioRoute.SystemPlayback)
        {
            _systemPlayback.Add(frame);
        }
        else
        {
            _microphone.Add(frame);
        }
    }

    public PcmAudioFrame? Select()
    {
        var system = Combine(_systemPlayback, ClassroomAudioRoute.SystemPlayback);
        var microphone = Combine(_microphone, ClassroomAudioRoute.Microphone);
        return system is { } activeSystem && activeSystem.RootMeanSquare >= systemActivityThreshold
            ? activeSystem
            : microphone ?? system;
    }

    private static PcmAudioFrame? Combine(
        IReadOnlyCollection<PcmAudioFrame> frames,
        ClassroomAudioRoute route)
    {
        if (frames.Count == 0)
        {
            return null;
        }

        var totalLength = frames.Sum(frame => frame.Pcm16.Length);
        var combined = GC.AllocateUninitializedArray<byte>(totalLength);
        var offset = 0;
        foreach (var frame in frames)
        {
            Buffer.BlockCopy(frame.Pcm16, 0, combined, offset, frame.Pcm16.Length);
            offset += frame.Pcm16.Length;
        }

        return new PcmAudioFrame(
            frames.First().TimeBucket,
            route,
            combined,
            CalculateRootMeanSquare(combined));
    }

    private static double CalculateRootMeanSquare(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length < 2)
        {
            return 0;
        }

        double sum = 0;
        var samples = pcm.Length / 2;
        for (var index = 0; index + 1 < pcm.Length; index += 2)
        {
            var sample = (short)(pcm[index] | pcm[index + 1] << 8);
            var normalized = sample / 32768d;
            sum += normalized * normalized;
        }

        return Math.Sqrt(sum / samples);
    }
}
