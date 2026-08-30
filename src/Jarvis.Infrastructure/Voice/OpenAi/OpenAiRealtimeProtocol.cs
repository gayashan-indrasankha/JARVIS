using System.Text.Json;
using Jarvis.Core.Voice;
using Jarvis.Infrastructure.Configuration;

namespace Jarvis.Infrastructure.Voice.OpenAi;

internal static class OpenAiRealtimeProtocol
{
    private static readonly string[] AudioOutputModalities = ["audio"];

    public static byte[] CreateSessionUpdate(
        RealtimeSessionConfiguration configuration,
        OpenAiRealtimeOptions options) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                type = "session.update",
                session = new
                {
                    type = "realtime",
                    model = options.Model,
                    output_modalities = AudioOutputModalities,
                    instructions = configuration.Instructions,
                    audio = new
                    {
                        input = new
                        {
                            format = new
                            {
                                type = "audio/pcm",
                                rate = AudioFormat.Pcm16Mono24Khz.SampleRateHz,
                            },
                            turn_detection = configuration.ActivationMode ==
                                VoiceActivationMode.ServerVoiceActivityDetection
                                ? new { type = "semantic_vad" }
                                : null,
                        },
                        output = new
                        {
                            format = new
                            {
                                type = "audio/pcm",
                                rate = AudioFormat.Pcm16Mono24Khz.SampleRateHz,
                            },
                            voice = options.Voice,
                        },
                    },
                },
            });

    public static byte[] CreateAudioAppend(ReadOnlySpan<byte> audio) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                type = "input_audio_buffer.append",
                audio = Convert.ToBase64String(audio),
            });

    public static IReadOnlyList<byte[]> CreateTextTurn(string text) =>
        [
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    type = "conversation.item.create",
                    item = new
                    {
                        type = "message",
                        role = "user",
                        content = new[]
                        {
                            new
                            {
                                type = "input_text",
                                text,
                            },
                        },
                    },
                }),
            CreateResponseRequest(),
        ];

    public static IReadOnlyList<byte[]> CreateAudioCommit() =>
        [
            JsonSerializer.SerializeToUtf8Bytes(
                new { type = "input_audio_buffer.commit" }),
            CreateResponseRequest(),
        ];

    public static byte[] CreateResponseCancellation() =>
        JsonSerializer.SerializeToUtf8Bytes(new { type = "response.cancel" });

    public static byte[] CreateTruncation(PlaybackCursor cursor)
    {
        long playedMilliseconds = Math.Max(0, (long)cursor.PlayedDuration.TotalMilliseconds);
        return JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                type = "conversation.item.truncate",
                item_id = cursor.ItemId,
                content_index = cursor.ContentIndex,
                audio_end_ms = playedMilliseconds,
            });
    }

    private static byte[] CreateResponseRequest() =>
        JsonSerializer.SerializeToUtf8Bytes(new { type = "response.create" });
}
