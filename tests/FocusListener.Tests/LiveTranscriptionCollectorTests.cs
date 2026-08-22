namespace FocusListener.Tests;

public sealed class LiveTranscriptionCollectorTests
{
    [Fact]
    public void ModelTurnCanCompleteBeforeInputTranscriptionFinishes()
    {
        var collector = new LiveTranscriptionCollector();

        var changedByTurn = collector.Apply(new LiveTranscriptionEvent(
            InterimText: null,
            FinalText: null,
            FinalFinished: false,
            ModelTurnComplete: true));

        Assert.False(changedByTurn);
        Assert.False(collector.IsFinished);
        Assert.Equal(string.Empty, collector.Text);

        Assert.True(collector.Apply(new LiveTranscriptionEvent(
            InterimText: "相遇时间是从出发",
            FinalText: null,
            FinalFinished: false,
            ModelTurnComplete: false)));
        Assert.False(collector.IsFinished);
        Assert.Equal("相遇时间是从出发", collector.Text);

        Assert.True(collector.Apply(new LiveTranscriptionEvent(
            InterimText: null,
            FinalText: "相遇时间是从同时出发到彼此相遇所经历的时间。",
            FinalFinished: true,
            ModelTurnComplete: false)));
        Assert.True(collector.IsFinished);
        Assert.Equal("相遇时间是从同时出发到彼此相遇所经历的时间。", collector.Text);
    }
}
