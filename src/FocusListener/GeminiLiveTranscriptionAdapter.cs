using System.Runtime.CompilerServices;
using System.Text;
using Google.GenAI;
using Google.GenAI.Types;

namespace FocusListener;

internal sealed record TranscriptUnit(
    string Text,
    DateTimeOffset RecognizedAt,
    TimeSpan RelativeStart);

internal interface IClassroomTranscriber
{
    IAsyncEnumerable<TranscriptUnit> TranscribeAsync(
        IAsyncEnumerable<PcmAudioFrame> audio,
        CancellationToken cancellation);
}

internal sealed class GeminiLiveTranscriptionAdapter(
    GeminiFocusOptions options,
    ISessionClock clock) : IClassroomTranscriber
{
    public async IAsyncEnumerable<TranscriptUnit> TranscribeAsync(
        IAsyncEnumerable<PcmAudioFrame> audio,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
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
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var startedAt = clock.UtcNow;
        var sendTask = SendAudioAsync(session, audio, lifetime.Token);
        var accumulator = new TranscriptAccumulator();

        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var message = await session.ReceiveAsync(cancellation);
                if (message is null)
                {
                    break;
                }

                var content = message.ServerContent;
                var transcription = content?.InputTranscription;
                if (!string.IsNullOrWhiteSpace(transcription?.Text))
                {
                    accumulator.Push(transcription.Text);
                }

                var boundary = transcription?.Finished == true || content?.TurnComplete == true || accumulator.HasNaturalBoundary;
                if (boundary && accumulator.TryTake(out var text))
                {
                    var recognizedAt = clock.UtcNow;
                    yield return new TranscriptUnit(text, recognizedAt, recognizedAt - startedAt);
                }
            }

            if (accumulator.TryTake(out var remainder))
            {
                var recognizedAt = clock.UtcNow;
                yield return new TranscriptUnit(remainder, recognizedAt, recognizedAt - startedAt);
            }
        }
        finally
        {
            lifetime.Cancel();
            await IgnoreCancellationAsync(sendTask);
            await session.CloseAsync();
        }
    }

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
