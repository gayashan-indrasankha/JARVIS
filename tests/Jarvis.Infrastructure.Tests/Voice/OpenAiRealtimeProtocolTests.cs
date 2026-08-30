using System.Text.Json;
using Jarvis.Core.Voice;
using Jarvis.Infrastructure.Configuration;
using Jarvis.Infrastructure.Voice.OpenAi;

namespace Jarvis.Infrastructure.Tests.Voice;

public sealed class OpenAiRealtimeProtocolTests
{
    [Fact]
    public void SessionUpdateUsesPcmAudioAndServerVadWithoutCredential()
    {
        OpenAiRealtimeOptions options = CreateOptions();
        RealtimeSessionConfiguration configuration = new(
            VoiceActivationMode.ServerVoiceActivityDetection,
            "Test instructions");

        byte[] payload = OpenAiRealtimeProtocol.CreateSessionUpdate(configuration, options);
        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement session = document.RootElement.GetProperty("session");

        Assert.Equal("session.update", document.RootElement.GetProperty("type").GetString());
        Assert.Equal("gpt-realtime-test", session.GetProperty("model").GetString());
        Assert.Equal(
            "semantic_vad",
            session.GetProperty("audio")
                .GetProperty("input")
                .GetProperty("turn_detection")
                .GetProperty("type")
                .GetString());
        Assert.Equal(
            24_000,
            session.GetProperty("audio")
                .GetProperty("input")
                .GetProperty("format")
                .GetProperty("rate")
                .GetInt32());
        Assert.DoesNotContain("test-secret", System.Text.Encoding.UTF8.GetString(payload));
    }

    [Fact]
    public void SessionUpdateDisablesTurnDetectionForPushToTalk()
    {
        byte[] payload = OpenAiRealtimeProtocol.CreateSessionUpdate(
            new RealtimeSessionConfiguration(
                VoiceActivationMode.PushToTalk,
                "Test instructions"),
            CreateOptions());
        using JsonDocument document = JsonDocument.Parse(payload);

        JsonElement turnDetection = document.RootElement
            .GetProperty("session")
            .GetProperty("audio")
            .GetProperty("input")
            .GetProperty("turn_detection");

        Assert.Equal(JsonValueKind.Null, turnDetection.ValueKind);
    }

    [Fact]
    public void TruncationBindsToAudibleItemAndMilliseconds()
    {
        PlaybackCursor cursor = new(
            "assistant-item",
            2,
            TimeSpan.FromMilliseconds(412.9));

        byte[] payload = OpenAiRealtimeProtocol.CreateTruncation(cursor);
        using JsonDocument document = JsonDocument.Parse(payload);

        Assert.Equal("conversation.item.truncate", document.RootElement.GetProperty("type").GetString());
        Assert.Equal("assistant-item", document.RootElement.GetProperty("item_id").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("content_index").GetInt32());
        Assert.Equal(412, document.RootElement.GetProperty("audio_end_ms").GetInt64());
    }

    private static OpenAiRealtimeOptions CreateOptions() =>
        new()
        {
            ApiKey = "test-secret",
            Model = "gpt-realtime-test",
            Voice = "marin",
        };
}
