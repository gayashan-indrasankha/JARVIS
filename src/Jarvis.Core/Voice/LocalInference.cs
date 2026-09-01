namespace Jarvis.Core.Voice;

/// <summary>
/// Provider-neutral language generation boundary. Implementations must not expose transport types.
/// </summary>
public interface ILanguageModel : IAsyncDisposable
{
    public ValueTask InitializeAsync(CancellationToken cancellationToken);

    public IAsyncEnumerable<LanguageModelToken> GenerateAsync(
        LanguageModelRequest request,
        CancellationToken cancellationToken);
}

public sealed record LanguageModelRequest
{
    public LanguageModelRequest(
        IReadOnlyList<ConversationMessage> messages,
        int maximumOutputTokens)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentOutOfRangeException.ThrowIfLessThan(messages.Count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            messages.Count,
            VoiceDataLimits.MaximumConversationMessages);
        ConversationMessage[] ownedMessages = messages.ToArray();
        if (ownedMessages.Any(static message => message is null))
        {
            throw new ArgumentException("Conversation messages cannot contain null values.", nameof(messages));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maximumOutputTokens, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumOutputTokens, 4_096);

        Messages = ownedMessages;
        MaximumOutputTokens = maximumOutputTokens;
    }

    public IReadOnlyList<ConversationMessage> Messages { get; }

    public int MaximumOutputTokens { get; }
}

public sealed record ConversationMessage
{
    public ConversationMessage(ConversationRole role, string text)
    {
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        int maximumCharacters = role == ConversationRole.System
            ? VoiceDataLimits.MaximumInstructionsCharacters
            : VoiceDataLimits.MaximumTextCharacters;
        if (text.Length > maximumCharacters ||
            text.Any(static character => character == '\0'))
        {
            throw new ArgumentException("Conversation text is invalid or too large.", nameof(text));
        }

        Role = role;
        Text = text;
    }

    public ConversationRole Role { get; }

    public string Text { get; }
}

public enum ConversationRole
{
    System,
    User,
    Assistant,
}

public sealed record LanguageModelToken
{
    public LanguageModelToken(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > VoiceDataLimits.MaximumTextCharacters)
        {
            throw new ArgumentException("A generated token chunk is too large.", nameof(text));
        }

        Text = text;
    }

    public string Text { get; }
}

public interface IVoiceActivityDetector : IAsyncDisposable
{
    public AudioFormat InputFormat { get; }

    public ValueTask InitializeAsync(CancellationToken cancellationToken);

    public ValueTask<VoiceActivityChange> ProcessAsync(
        AudioFrame frame,
        CancellationToken cancellationToken);

    public ValueTask ResetAsync(CancellationToken cancellationToken);
}

public enum VoiceActivityChange
{
    None,
    SpeechStarted,
    SpeechEnded,
}

public interface ISpeechRecognizer : IAsyncDisposable
{
    public AudioFormat InputFormat { get; }

    public ValueTask InitializeAsync(CancellationToken cancellationToken);

    public ValueTask<SpeechRecognitionUpdate?> ProcessAudioAsync(
        AudioFrame frame,
        CancellationToken cancellationToken);

    public ValueTask<SpeechRecognitionResult> CompleteUtteranceAsync(
        CancellationToken cancellationToken);

    public ValueTask ResetAsync(CancellationToken cancellationToken);
}

public sealed record SpeechRecognitionUpdate(string Text);

public sealed record SpeechRecognitionResult(string Text);

public interface ISpeechSynthesizer : IAsyncDisposable
{
    public AudioFormat OutputFormat { get; }

    public ValueTask InitializeAsync(CancellationToken cancellationToken);

    public IAsyncEnumerable<SynthesizedAudioChunk> SynthesizeAsync(
        SpeechSynthesisRequest request,
        CancellationToken cancellationToken);
}

public sealed record SpeechSynthesisRequest
{
    public SpeechSynthesisRequest(string text, long generationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generationId);
        if (text.Length > VoiceDataLimits.MaximumSpeechSegmentCharacters)
        {
            throw new ArgumentException("A speech segment is too large.", nameof(text));
        }

        Text = text;
        GenerationId = generationId;
    }

    public string Text { get; }

    public long GenerationId { get; }
}

public sealed record SynthesizedAudioChunk
{
    public SynthesizedAudioChunk(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0 || data.Length > VoiceDataLimits.MaximumAudioChunkBytes)
        {
            throw new ArgumentException("Synthesized audio has an invalid size.", nameof(data));
        }

        Data = data;
    }

    public byte[] Data { get; }
}

public sealed class LocalComponentUnavailableException : InvalidOperationException
{
    public LocalComponentUnavailableException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (code.Length > 64 ||
            code.Any(static character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
        {
            throw new ArgumentException("The component error code is invalid.", nameof(code));
        }

        Code = code;
    }

    public string Code { get; }
}
