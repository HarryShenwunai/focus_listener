using System.Runtime.CompilerServices;
using System.Text;
using Google.GenAI;
using Google.GenAI.Types;

namespace FocusListener;

internal sealed record TranscriptUnit(
    string Text,
    DateTimeOffset RecognizedAt,
    TimeSpan RelativeStart)
{
    public string AudioSource { get; init; } = "未知";
}

internal interface IClassroomTranscriber
{
    IAsyncEnumerable<TranscriptUnit> TranscribeAsync(
        IAsyncEnumerable<PcmAudioFrame> audio,
        CancellationToken cancellation);
}

internal sealed class GeminiLiveTranscriptionAdapter(
    GeminiFocusOptions options,
    ISessionClock clock,
    ClassroomExperienceControl? experience = null) : IClassroomTranscriber
{
    public async IAsyncEnumerable<TranscriptUnit> TranscribeAsync(
        IAsyncEnumerable<PcmAudioFrame> audio,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        Report(LiveTranscriptState.Connecting, "正在连接 Gemini Live…", string.Empty, string.Empty);
        using var client = new Client(apiKey: options.ApiKey);
        var config = new LiveConnectConfig
        {
            ResponseModalities = [Modality.Audio],
            InputAudioTranscription = new AudioTranscriptionConfig(),
            SystemInstruction = new Content
            {
                Parts =
                [
                    new Part
                    {
                        Text = "只监听并转写输入课堂音频，不向用户提问，也不要进行口头回复。保留中英文原词。"
                    }
                ]
            }
        };

        await using var session = await client.Live.ConnectAsync(options.LiveModel, config, cancellation);
        Report(LiveTranscriptState.Listening, "正在听课，等待声音…", string.Empty, string.Empty);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var startedAt = clock.UtcNow;
        var sendTask = SendAudioAsync(session, audio, lifetime.Token);
        var accumulator = new TranscriptAccumulator();
        var collector = new LiveTranscriptionCollector();

        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var message = await session.ReceiveAsync(cancellation);
                if (message is null)
                {
                    throw new IOException("Gemini Live 在课堂仍进行时关闭了转写连接。");
                }

                var content = message.ServerContent;
                var interim = content?.InterimInputTranscription;
                var final = content?.InputTranscription;
                if (!string.IsNullOrWhiteSpace(final?.Text))
                {
                    accumulator.Push(final.Text);
                }

                var changed = collector.Apply(new LiveTranscriptionEvent(
                    interim?.Text,
                    final?.Text,
                    final?.Finished == true,
                    content?.TurnComplete == true));
                if (changed)
                {
                    Report(
                        LiveTranscriptState.Listening,
                        final is not null ? "已确认课堂文字" : "正在形成字幕…",
                        collector.CommittedText,
                        collector.InterimText);
                }

                var boundary = final?.Finished == true ||
                               content?.TurnComplete == true ||
                               accumulator.HasNaturalBoundary;
                if (boundary && accumulator.TryTake(out var text))
                {
                    var recognizedAt = clock.UtcNow;
                    yield return new TranscriptUnit(text, recognizedAt, recognizedAt - startedAt)
                    {
                        AudioSource = experience?.LastAudioActivity?.Source ?? "未知"
                    };
                }
            }

            if (accumulator.TryTake(out var remainder))
            {
                var recognizedAt = clock.UtcNow;
                yield return new TranscriptUnit(remainder, recognizedAt, recognizedAt - startedAt)
                {
                    AudioSource = experience?.LastAudioActivity?.Source ?? "未知"
                };
            }
        }
        finally
        {
            lifetime.Cancel();
            await IgnoreCancellationAsync(sendTask);
            try
            {
                await session.CloseAsync();
            }
            catch (Exception exception) when (exception is not OperationCanceledException && cancellation.IsCancellationRequested)
            {
            }
        }
    }

    private void Report(
        LiveTranscriptState state,
        string status,
        string committed,
        string interim) => experience?.ReportTranscript(new LiveTranscriptPreview(
            committed,
            interim,
            state,
            status,
            clock.UtcNow));

    private static async Task SendAudioAsync(
        AsyncSession session,
        IAsyncEnumerable<PcmAudioFrame> audio,
        CancellationToken cancellation)
    {
        try
        {
            await foreach (var frame in audio.WithCancellation(cancellation))
            {
                await session.SendRealtimeInputAsync(
                    new LiveSendRealtimeInputParameters
                    {
                        Audio = new Blob
                        {
                            Data = frame.Pcm16,
                            MimeType = "audio/pcm;rate=16000"
                        }
                    },
                    cancellation);
            }

            await session.SendRealtimeInputAsync(
                new LiveSendRealtimeInputParameters { AudioStreamEnd = true },
                cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class TranscriptAccumulator
    {
        private readonly StringBuilder _text = new();

        public bool HasNaturalBoundary =>
            _text.Length >= 60 ||
            (_text.Length >= 18 && "。！？.!?".Contains(_text[^1]));

        public void Push(string update)
        {
            var normalized = update.Trim();
            if (normalized.Length == 0)
            {
                return;
            }

            var current = _text.ToString();
            if (normalized.StartsWith(current, StringComparison.Ordinal))
            {
                _text.Clear();
                _text.Append(normalized);
            }
            else if (!current.EndsWith(normalized, StringComparison.Ordinal))
            {
                if (_text.Length > 0 && !char.IsPunctuation(_text[^1]))
                {
                    _text.Append(' ');
                }

                _text.Append(normalized);
            }
        }

        public bool TryTake(out string text)
        {
            text = _text.ToString().Trim();
            _text.Clear();
            return text.Length >= 12;
        }
    }
}
