using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jarvis.Core.Voice;

namespace Jarvis.Infrastructure.Voice.Local.Llama;

internal sealed class LlamaCppLocalLanguageModel : ILanguageModel
{
    private const int MaximumEventLineCharacters = 64 * 1024;
    private readonly ILlamaServerSupervisor _supervisor;
    private readonly ILoopbackHttpClientFactory _httpClientFactory;
    private readonly IVoiceMetrics _metrics;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private HttpClient? _client;
    private LlamaServerConnection? _connection;
    private bool _disposed;

    public LlamaCppLocalLanguageModel(
        ILlamaServerSupervisor supervisor,
        ILoopbackHttpClientFactory httpClientFactory,
        IVoiceMetrics metrics)
    {
        _supervisor = supervisor;
        _httpClientFactory = httpClientFactory;
        _metrics = metrics;
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stopwatch readiness = Stopwatch.StartNew();
        bool initializedNewClient = false;
        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LlamaServerConnection connection = await _supervisor
                .EnsureReadyAsync(cancellationToken)
                .ConfigureAwait(false);
            if (_connection != connection || _client is null)
            {
                _client?.Dispose();
                _client = _httpClientFactory.Create(
                    connection.Endpoint,
                    connection.AuthenticationToken);
                _connection = connection;
                initializedNewClient = true;
            }
        }
        finally
        {
            _initializationGate.Release();
        }

        if (initializedNewClient)
        {
            _metrics.Record(new VoiceMetric(
                VoiceMetricKind.LanguageModelReady,
                readiness.Elapsed.TotalMilliseconds));
        }
    }

    public async IAsyncEnumerable<LanguageModelToken> GenerateAsync(
        LanguageModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        HttpClient client = _client ?? throw new InvalidOperationException(
            "The local language model client was not initialized.");

        LlamaChatRequest payload = CreateRequest(request);
        using HttpRequestMessage message = new(HttpMethod.Post, "v1/chat/completions")
        {
            Content = JsonContent.Create(payload),
        };

        Stopwatch generation = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await client
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            throw new LocalComponentUnavailableException(
                "local_llm_unavailable",
                "The local language model stopped responding.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new LocalComponentUnavailableException(
                    "local_llm_request_failed",
                    "The local language model rejected the generation request.");
            }

            await using Stream stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using StreamReader reader = new(stream);
            bool firstToken = true;
            int emittedCharacters = 0;
            await foreach (string line in ReadBoundedLinesAsync(reader, cancellationToken)
                .ConfigureAwait(false))
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                string data = line[5..].TrimStart();
                if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
                {
                    break;
                }

                string? content = ParseVisibleContent(data);
                if (string.IsNullOrEmpty(content))
                {
                    TryRecordServerTimings(data);
                    continue;
                }

                emittedCharacters = checked(emittedCharacters + content.Length);
                if (emittedCharacters > VoiceDataLimits.MaximumTextCharacters)
                {
                    throw new InvalidDataException("The local language model response exceeded its size limit.");
                }

                if (firstToken)
                {
                    firstToken = false;
                    _metrics.Record(new VoiceMetric(
                        VoiceMetricKind.FirstToken,
                        generation.Elapsed.TotalMilliseconds));
                    _metrics.Record(new VoiceMetric(
                        VoiceMetricKind.WarmLanguageModelFirstToken,
                        generation.Elapsed.TotalMilliseconds));
                }

                yield return new LanguageModelToken(content);
            }
        }
    }

    private static async IAsyncEnumerable<string> ReadBoundedLinesAsync(
        StreamReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        char[] buffer = new char[4 * 1024];
        System.Text.StringBuilder line = new();
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                if (line.Length > 0)
                {
                    yield return line.ToString().TrimEnd('\r');
                }

                yield break;
            }

            for (int index = 0; index < read; index++)
            {
                char character = buffer[index];
                if (character == '\n')
                {
                    yield return line.ToString().TrimEnd('\r');
                    line.Clear();
                    continue;
                }

                line.Append(character);
                if (line.Length > MaximumEventLineCharacters)
                {
                    throw new InvalidDataException(
                        "The local inference event exceeded its size limit.");
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client?.Dispose();
        _initializationGate.Dispose();
        await _supervisor.DisposeAsync().ConfigureAwait(false);
    }

    private static LlamaChatRequest CreateRequest(LanguageModelRequest request)
    {
        List<LlamaChatMessage> messages = request.Messages
            .Select(static message => new LlamaChatMessage(
                message.Role switch
                {
                    ConversationRole.System => "system",
                    ConversationRole.User => "user",
                    ConversationRole.Assistant => "assistant",
                    _ => throw new InvalidOperationException("Unsupported conversation role."),
                },
                message.Text))
            .ToList();

        int lastUser = messages.FindLastIndex(
            static message => string.Equals(message.Role, "user", StringComparison.Ordinal));
        if (lastUser >= 0 &&
            !messages[lastUser].Content.Contains("/no_think", StringComparison.Ordinal))
        {
            messages[lastUser] = messages[lastUser] with
            {
                Content = messages[lastUser].Content + "\n/no_think",
            };
        }

        return new LlamaChatRequest(
            LocalAssetPaths.SupportedLanguageModelId,
            messages,
            request.MaximumOutputTokens,
            Stream: true,
            Temperature: 0.7F,
            TopP: 0.8F,
            TopK: 20,
            PresencePenalty: 1.5F);
    }

    private static string? ParseVisibleContent(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("choices", out JsonElement choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0 ||
                !choices[0].TryGetProperty("delta", out JsonElement delta) ||
                !delta.TryGetProperty("content", out JsonElement content) ||
                content.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return content.GetString();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The local inference stream returned invalid JSON.", exception);
        }
    }

    private void TryRecordServerTimings(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("timings", out JsonElement timings) &&
                timings.TryGetProperty("prompt_ms", out JsonElement promptMilliseconds) &&
                promptMilliseconds.TryGetDouble(out double value) &&
                double.IsFinite(value) &&
                value >= 0)
            {
                _metrics.Record(new VoiceMetric(VoiceMetricKind.PromptProcessing, value));
            }

            if (root.TryGetProperty("timings", out timings) &&
                timings.TryGetProperty("predicted_per_second", out JsonElement rate) &&
                rate.TryGetDouble(out value) &&
                double.IsFinite(value) &&
                value >= 0)
            {
                _metrics.Record(new VoiceMetric(VoiceMetricKind.TokensPerSecond, value));
            }
        }
        catch (JsonException)
        {
            // A malformed event is handled by the main parser if it contains visible content.
        }
    }

    private sealed record LlamaChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<LlamaChatMessage> Messages,
        [property: JsonPropertyName("max_tokens")] int MaximumTokens,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("temperature")] float Temperature,
        [property: JsonPropertyName("top_p")] float TopP,
        [property: JsonPropertyName("top_k")] int TopK,
        [property: JsonPropertyName("presence_penalty")] float PresencePenalty);

    private sealed record LlamaChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);
}
