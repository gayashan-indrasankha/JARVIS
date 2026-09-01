using Jarvis.Core.Voice;
using Jarvis.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NAudio;
using NAudio.Wave;

namespace Jarvis.Infrastructure.Voice;

internal sealed class WindowsSpeakerPlayback : IAudioPlayback
{
    private readonly AudioDeviceOptions _options;
    private readonly ILogger<WindowsSpeakerPlayback> _logger;
    private readonly Lock _sync = new();
    private BufferedWaveProvider? _buffer;
    private WaveOut? _speaker;
    private long _generationId;
    private long _invalidThroughGenerationId;
    private int _disposed;

    public WindowsSpeakerPlayback(
        IOptions<VoiceOptions> options,
        ILogger<WindowsSpeakerPlayback> logger)
    {
        _options = options.Value.Audio;
        _logger = logger;
    }

    public AudioFormat Format => AudioFormat.Pcm16Mono24Khz;

    public async ValueTask EnqueueAsync(
        AssistantAudioChunk chunk,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (chunk.Data.Length == 0)
        {
            return;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_sync)
            {
                if (chunk.GenerationId <= _invalidThroughGenerationId)
                {
                    return;
                }

                EnsureInitialized();

                if (chunk.Data.Length > _buffer!.BufferLength)
                {
                    throw new InvalidDataException("Assistant audio chunk exceeds the playback buffer limit.");
                }

                if (_buffer.BufferedBytes + chunk.Data.Length <= _buffer.BufferLength)
                {
                    if (_generationId != 0 && _generationId != chunk.GenerationId)
                    {
                        TryStopSpeaker();
                        _buffer.ClearBuffer();
                    }

                    _generationId = chunk.GenerationId;
                    _buffer.AddSamples(chunk.Data, 0, chunk.Data.Length);

                    if (_speaker!.PlaybackState != PlaybackState.Playing)
                    {
                        _speaker.Play();
                    }

                    return;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask InterruptAsync(
        long invalidThroughGenerationId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(invalidThroughGenerationId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            _invalidThroughGenerationId = Math.Max(
                _invalidThroughGenerationId,
                invalidThroughGenerationId);
            TryStopSpeaker();
            _buffer?.ClearBuffer();
            _generationId = 0;
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            _buffer?.ClearBuffer();
            TryStopSpeaker();
            _generationId = 0;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        lock (_sync)
        {
            _speaker?.Dispose();
            _speaker = null;
            _buffer = null;
        }

        return ValueTask.CompletedTask;
    }

    private void EnsureInitialized()
    {
        if (_speaker is not null)
        {
            return;
        }

        WaveFormat waveFormat = new(
            Format.SampleRateHz,
            Format.BitsPerSample,
            Format.ChannelCount);
        _buffer = new BufferedWaveProvider(
            waveFormat,
            TimeSpan.FromMilliseconds(_options.MaximumPlaybackBufferMilliseconds))
        {
            DiscardOnBufferOverflow = false,
            ReadFully = true,
        };
        _speaker = new WaveOut
        {
            DeviceNumber = _options.OutputDeviceNumber,
            BufferMilliseconds = 50,
            NumberOfBuffers = 3,
        };
        _speaker.Init(_buffer);
        WindowsAudioLog.PlaybackStarted(
            _logger,
            Format.SampleRateHz,
            Format.ChannelCount,
            Format.BitsPerSample);
    }

    private void TryStopSpeaker()
    {
        try
        {
            _speaker?.Stop();
        }
        catch (MmException exception)
        {
            WindowsAudioLog.PlaybackOperationFailed(_logger, exception.GetType().Name);
        }
    }
}
