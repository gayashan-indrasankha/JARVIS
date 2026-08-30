using System.Text.Json;
using Jarvis.Core.Voice;

namespace Jarvis.Infrastructure.Voice.OpenAi;

internal sealed class OpenAiRealtimeEventParser
{
    private string? _currentItemId;

    public RealtimeConversationEvent? Parse(ReadOnlyMemory<byte> payload)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;

            if (!TryGetString(root, "type", out string? eventType))
            {
                return new RealtimeProviderErrorEvent("protocol_missing_event_type", IsTransient: false);
            }

            return eventType switch
            {
                "session.updated" => new RealtimeConnectedEvent(),
                "input_audio_buffer.speech_started" => new UserSpeechStartedEvent(),
                "input_audio_buffer.speech_stopped" => new UserSpeechStoppedEvent(),
                "response.output_item.added" => CaptureOutputItem(root),
                "response.output_audio.delta" => ParseAudioDelta(root),
                "response.output_audio_transcript.delta" => ParseTranscriptDelta(root),
                "response.done" => ParseResponseCompleted(root),
                "error" => ParseError(root),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return new RealtimeProviderErrorEvent("protocol_invalid_json", IsTransient: false);
        }
        catch (FormatException)
        {
            return new RealtimeProviderErrorEvent("protocol_invalid_audio", IsTransient: false);
        }
        catch (ArgumentException)
        {
            return new RealtimeProviderErrorEvent("protocol_invalid_event", IsTransient: false);
        }
    }

    private RealtimeConversationEvent? CaptureOutputItem(JsonElement root)
    {
        if (root.TryGetProperty("item", out JsonElement item) &&
            TryGetString(item, "id", out string? itemId))
        {
            _currentItemId = itemId;
        }

        return null;
    }

    private RealtimeConversationEvent ParseAudioDelta(JsonElement root)
    {
        if (!TryGetString(root, "delta", out string? encodedAudio))
        {
            return new RealtimeProviderErrorEvent("protocol_missing_audio", IsTransient: false);
        }

        string? itemId = TryGetString(root, "item_id", out string? eventItemId)
            ? eventItemId
            : _currentItemId;

        if (string.IsNullOrWhiteSpace(itemId))
        {
            return new RealtimeProviderErrorEvent("protocol_missing_item_id", IsTransient: false);
        }

        int contentIndex = root.TryGetProperty("content_index", out JsonElement indexElement) &&
            indexElement.TryGetInt32(out int index)
            ? index
            : 0;

        return new AssistantAudioDeltaEvent(
            new AssistantAudioChunk(
                Convert.FromBase64String(encodedAudio!),
                itemId,
                contentIndex));
    }

    private static RealtimeConversationEvent ParseTranscriptDelta(JsonElement root)
    {
        if (!TryGetString(root, "delta", out string? text))
        {
            return new RealtimeProviderErrorEvent(
                "protocol_missing_transcript",
                IsTransient: false);
        }

        return text!.Length <= VoiceDataLimits.MaximumTextCharacters
            ? new AssistantTranscriptDeltaEvent(text)
            : new RealtimeProviderErrorEvent(
                "protocol_transcript_too_large",
                IsTransient: false);
    }

    private AssistantResponseCompletedEvent ParseResponseCompleted(JsonElement root)
    {
        string? itemId = null;
        if (root.TryGetProperty("response", out JsonElement response) &&
            response.TryGetProperty("output", out JsonElement output) &&
            output.ValueKind == JsonValueKind.Array &&
            output.GetArrayLength() > 0 &&
            TryGetString(output[0], "id", out string? completedItemId))
        {
            itemId = completedItemId;
        }

        itemId ??= _currentItemId;
        _currentItemId = null;
        return new AssistantResponseCompletedEvent(itemId);
    }

    private static RealtimeProviderErrorEvent ParseError(JsonElement root)
    {
        string code = "provider_error";
        if (root.TryGetProperty("error", out JsonElement error) &&
            TryGetString(error, "code", out string? errorCode))
        {
            code = SanitizeCode(errorCode!);
        }

        bool isTransient = code is "server_error" or "rate_limit_exceeded";
        return new RealtimeProviderErrorEvent(code, isTransient);
    }

    private static string SanitizeCode(string code)
    {
        if (code.Length is 0 or > 64 ||
            code.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')))
        {
            return "provider_error";
        }

        return code;
    }

    private static bool TryGetString(
        JsonElement element,
        string propertyName,
        out string? value)
    {
        if (element.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return value is not null;
        }

        value = null;
        return false;
    }
}
