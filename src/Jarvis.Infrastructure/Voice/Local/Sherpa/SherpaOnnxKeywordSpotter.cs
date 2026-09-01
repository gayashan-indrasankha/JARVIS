using System.Diagnostics;
using System.Runtime.CompilerServices;
using Jarvis.Core.Voice;
using Jarvis.Infrastructure.Configuration;
using Microsoft.Extensions.Options;
using SherpaOnnx;

namespace Jarvis.Infrastructure.Voice.Local.Sherpa;

/// <summary>
/// Runs a single-phrase, open-vocabulary sherpa-onnx keyword stream locally.
/// Dormant microphone frames are processed transiently and are never persisted.
/// </summary>
internal sealed class SherpaOnnxKeywordSpotter : IWakeWordDetector
{
    internal const string JarvisKeywordTokens = "▁JA R VI S @JARVIS";

    private readonly LocalAssetPaths _assets;
    private readonly IAudioCapture _capture;
    private readonly WakeWordOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private KeywordSpotter? _spotter;
    private int _active;
    private bool _disposed;

    public SherpaOnnxKeywordSpotter(
        LocalAssetPaths assets,
        IAudioCapture capture,
        IOptions<VoiceOptions> options,
        TimeProvider timeProvider)
    {
        _assets = assets;
        _capture = capture;
        _options = options.Value.WakeWord;
        _timeProvider = timeProvider;
    }

    public bool IsAvailable =>
        !_disposed &&
        File.Exists(_assets.WakeWordEncoder) &&
        File.Exists(_assets.WakeWordDecoder) &&
        File.Exists(_assets.WakeWordJoiner) &&
        File.Exists(_assets.WakeWordTokens);

    public async IAsyncEnumerable<WakeWordDetection> ListenAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
        {
            throw new InvalidOperationException("Wake-word listening is already active.");
        }

        WakeWordDetection? detection = null;
        try
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
            using OnlineStream stream = _spotter!.CreateStream(JarvisKeywordTokens);

            await foreach (AudioFrame frame in
                _capture.CaptureAsync(cancellationToken).ConfigureAwait(false))
            {
                long processingStarted = Stopwatch.GetTimestamp();
                stream.AcceptWaveform(
                    AudioFormat.Pcm16Mono16Khz.SampleRateHz,
                    PcmAudio.Pcm16ToFloat(frame.Data));
                while (_spotter.IsReady(stream))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _spotter.Decode(stream);
                }

                string keyword = _spotter.GetResult(stream).Keyword.Trim();
                if (keyword.Length == 0)
                {
                    continue;
                }

                detection = new WakeWordDetection(
                    _timeProvider.GetUtcNow(),
                    Stopwatch.GetElapsedTime(processingStarted));
                _spotter.Reset(stream);
                break;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _active, 0);
        }

        // Yield only after the capture enumerator has released the microphone so the
        // coordinator can safely hand it to the conversation capture pipeline.
        if (detection is not null)
        {
            yield return detection;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _initializationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            _spotter?.Dispose();
            _spotter = null;
        }
        finally
        {
            _initializationGate.Release();
            _initializationGate.Dispose();
        }
    }

    private async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_spotter is not null)
            {
                return;
            }

            if (!IsAvailable)
            {
                throw LocalAssetPaths.Missing("wake_word_model");
            }

            KeywordSpotterConfig configuration = new()
            {
                MaxActivePaths = 4,
                NumTrailingBlanks = 2,
                KeywordsScore = _options.KeywordScore,
                KeywordsThreshold = _options.KeywordThreshold,
            };
            configuration.FeatConfig.SampleRate = AudioFormat.Pcm16Mono16Khz.SampleRateHz;
            configuration.FeatConfig.FeatureDim = 80;
            configuration.ModelConfig.Transducer.Encoder = _assets.WakeWordEncoder;
            configuration.ModelConfig.Transducer.Decoder = _assets.WakeWordDecoder;
            configuration.ModelConfig.Transducer.Joiner = _assets.WakeWordJoiner;
            configuration.ModelConfig.Tokens = _assets.WakeWordTokens;
            configuration.ModelConfig.NumThreads = 1;
            configuration.ModelConfig.Provider = "cpu";
            configuration.ModelConfig.Debug = 0;
            _spotter = new KeywordSpotter(configuration);
        }
        finally
        {
            _initializationGate.Release();
        }
    }
}
