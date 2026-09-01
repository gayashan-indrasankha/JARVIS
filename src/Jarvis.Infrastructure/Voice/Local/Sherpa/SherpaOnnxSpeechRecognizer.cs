using Jarvis.Core.Voice;
using Jarvis.Infrastructure.Configuration;
using Microsoft.Extensions.Options;
using SherpaOnnx;

namespace Jarvis.Infrastructure.Voice.Local.Sherpa;

internal sealed class SherpaOnnxSpeechRecognizer : ISpeechRecognizer
{
    private readonly LocalAssetPaths _assets;
    private readonly VoiceOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OnlineRecognizer? _recognizer;
    private OnlineStream? _stream;
    private string _lastPartial = string.Empty;
    private bool _disposed;

    public SherpaOnnxSpeechRecognizer(
        LocalAssetPaths assets,
        IOptions<VoiceOptions> options)
    {
        _assets = assets;
        _options = options.Value;
    }

    public AudioFormat InputFormat => AudioFormat.Pcm16Mono16Khz;

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_recognizer is not null)
            {
                return;
            }

            if (!string.Equals(
                _options.SpeechRecognitionProfile,
                LocalAssetPaths.SupportedSpeechProfile,
                StringComparison.Ordinal))
            {
                throw new LocalComponentUnavailableException(
                    "speech_recognition_profile_unsupported",
                    "The configured Voice:SpeechRecognitionProfile is not present in the tracked model manifest.");
            }

            if (!File.Exists(_assets.SpeechEncoder) ||
                !File.Exists(_assets.SpeechDecoder) ||
                !File.Exists(_assets.SpeechJoiner) ||
                !File.Exists(_assets.SpeechTokens))
            {
                throw LocalAssetPaths.Missing("speech_recognition_model");
            }

            OnlineRecognizerConfig configuration = new()
            {
                DecodingMethod = "greedy_search",
                EnableEndpoint = 0,
            };
            configuration.FeatConfig.SampleRate = InputFormat.SampleRateHz;
            configuration.FeatConfig.FeatureDim = 80;
            configuration.ModelConfig.Transducer.Encoder = _assets.SpeechEncoder;
            configuration.ModelConfig.Transducer.Decoder = _assets.SpeechDecoder;
            configuration.ModelConfig.Transducer.Joiner = _assets.SpeechJoiner;
            configuration.ModelConfig.Tokens = _assets.SpeechTokens;
            configuration.ModelConfig.NumThreads = 2;
            configuration.ModelConfig.Provider = "cpu";
            configuration.ModelConfig.Debug = 0;
            _recognizer = new OnlineRecognizer(configuration);
            _stream = _recognizer.CreateStream();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<SpeechRecognitionUpdate?> ProcessAudioAsync(
        AudioFrame frame,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        float[] samples = PcmAudio.Pcm16ToFloat(frame.Data);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _stream!.AcceptWaveform(InputFormat.SampleRateHz, samples);
            while (_recognizer!.IsReady(_stream))
            {
                cancellationToken.ThrowIfCancellationRequested();
                _recognizer.Decode(_stream);
            }

            string text = _recognizer.GetResult(_stream).Text.Trim();
            if (text.Length == 0 || string.Equals(text, _lastPartial, StringComparison.Ordinal))
            {
                return null;
            }

            _lastPartial = text;
            return new SpeechRecognitionUpdate(text);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<SpeechRecognitionResult> CompleteUtteranceAsync(
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _stream!.InputFinished();
            while (_recognizer!.IsReady(_stream))
            {
                cancellationToken.ThrowIfCancellationRequested();
                _recognizer.Decode(_stream);
            }

            string text = _recognizer.GetResult(_stream).Text.Trim();
            _recognizer.Reset(_stream);
            _lastPartial = string.Empty;
            return new SpeechRecognitionResult(text);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask ResetAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _recognizer!.Reset(_stream!);
            _lastPartial = string.Empty;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            _stream?.Dispose();
            _recognizer?.Dispose();
            _stream = null;
            _recognizer = null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
