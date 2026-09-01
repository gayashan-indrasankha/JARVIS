using Jarvis.Core.Voice;
using Jarvis.Infrastructure.Configuration;
using Microsoft.Extensions.Options;
using SherpaOnnx;

namespace Jarvis.Infrastructure.Voice.Local.Sherpa;

internal sealed class SherpaOnnxVoiceActivityDetector : IVoiceActivityDetector
{
    private const int WindowSize = 512;
    private readonly LocalAssetPaths _assets;
    private readonly VoiceActivityDetectionOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly float[] _pendingSamples = new float[WindowSize];
    private VoiceActivityDetector? _detector;
    private int _pendingCount;
    private bool _speechDetected;
    private bool _disposed;

    public SherpaOnnxVoiceActivityDetector(
        LocalAssetPaths assets,
        IOptions<VoiceOptions> options)
    {
        _assets = assets;
        _options = options.Value.VoiceActivityDetection;
    }

    public AudioFormat InputFormat => AudioFormat.Pcm16Mono16Khz;

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_detector is not null)
            {
                return;
            }

            if (!File.Exists(_assets.VadModel))
            {
                throw LocalAssetPaths.Missing("vad_model");
            }

            VadModelConfig configuration = new()
            {
                SampleRate = InputFormat.SampleRateHz,
                NumThreads = 1,
                Provider = "cpu",
                Debug = 0,
            };
            configuration.SileroVad.Model = _assets.VadModel;
            configuration.SileroVad.Threshold = _options.Threshold;
            configuration.SileroVad.MinSilenceDuration = _options.MinimumSilenceSeconds;
            configuration.SileroVad.MinSpeechDuration = _options.MinimumSpeechSeconds;
            configuration.SileroVad.MaxSpeechDuration = _options.MaximumSpeechSeconds;
            configuration.SileroVad.WindowSize = WindowSize;
            float bufferSizeInSeconds = Math.Max(35.0F, _options.MaximumSpeechSeconds + 5.0F);
            _detector = new VoiceActivityDetector(configuration, bufferSizeInSeconds);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<VoiceActivityChange> ProcessAsync(
        AudioFrame frame,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        float[] samples = PcmAudio.Pcm16ToFloat(frame.Data);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            VoiceActivityChange change = VoiceActivityChange.None;
            int sourceOffset = 0;
            while (sourceOffset < samples.Length)
            {
                int count = Math.Min(WindowSize - _pendingCount, samples.Length - sourceOffset);
                Array.Copy(samples, sourceOffset, _pendingSamples, _pendingCount, count);
                sourceOffset += count;
                _pendingCount += count;
                if (_pendingCount != WindowSize)
                {
                    continue;
                }

                _detector!.AcceptWaveform(_pendingSamples);
                _pendingCount = 0;
                bool nowDetected = _detector.IsSpeechDetected();
                if (nowDetected && !_speechDetected)
                {
                    _speechDetected = true;
                    change = VoiceActivityChange.SpeechStarted;
                }

                if (!_detector.IsEmpty())
                {
                    while (!_detector.IsEmpty())
                    {
                        _detector.Pop();
                    }

                    if (_speechDetected)
                    {
                        _speechDetected = false;
                        change = VoiceActivityChange.SpeechEnded;
                    }
                }
            }

            return change;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask ResetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _detector?.Reset();
            _detector?.Clear();
            _pendingCount = 0;
            _speechDetected = false;
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
            _detector?.Dispose();
            _detector = null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
