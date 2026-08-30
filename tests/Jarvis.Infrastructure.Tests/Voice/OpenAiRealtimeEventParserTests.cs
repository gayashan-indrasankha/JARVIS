using System.Text;
using Jarvis.Core.Voice;
using Jarvis.Infrastructure.Voice.OpenAi;

namespace Jarvis.Infrastructure.Tests.Voice;

public sealed class OpenAiRealtimeEventParserTests
{
    [Fact]
    public void AudioDeltaDecodesOwnedPcmBufferAndItemIdentity()
    {
        OpenAiRealtimeEventParser parser = new();
        byte[] pcm = [1, 3, 5, 7];
        byte[] payload = Encoding.UTF8.GetBytes(
            $$"""{"type":"response.output_audio.delta","item_id":"item-1","content_index":3,"delta":"{{Convert.ToBase64String(pcm)}}"}""");

        AssistantAudioDeltaEvent audio = Assert.IsType<AssistantAudioDeltaEvent>(
            parser.Parse(payload));

        Assert.Equal(pcm, audio.Chunk.Data);
        Assert.Equal("item-1", audio.Chunk.ItemId);
        Assert.Equal(3, audio.Chunk.ContentIndex);
    }

    [Fact]
    public void MalformedJsonReturnsSanitizedProtocolError()
    {
        OpenAiRealtimeEventParser parser = new();

        RealtimeProviderErrorEvent error = Assert.IsType<RealtimeProviderErrorEvent>(
            parser.Parse("not-json"u8.ToArray()));

        Assert.Equal("protocol_invalid_json", error.Code);
        Assert.False(error.IsTransient);
    }

    [Fact]
    public void ProviderErrorExposesCodeButNotRemoteMessage()
    {
        OpenAiRealtimeEventParser parser = new();
        byte[] payload =
            """{"type":"error","error":{"code":"invalid_request","message":"private echoed input"}}"""u8.ToArray();

        RealtimeProviderErrorEvent error = Assert.IsType<RealtimeProviderErrorEvent>(
            parser.Parse(payload));

        Assert.Equal("invalid_request", error.Code);
    }

    [Fact]
    public void ProviderErrorReplacesUnsafeCode()
    {
        OpenAiRealtimeEventParser parser = new();
        byte[] payload =
            """{"type":"error","error":{"code":"bad\u001b[2Jcode"}}"""u8.ToArray();

        RealtimeProviderErrorEvent error = Assert.IsType<RealtimeProviderErrorEvent>(
            parser.Parse(payload));

        Assert.Equal("provider_error", error.Code);
    }
}
