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
    private string? _itemId;
    private int _contentIndex;
    private long _bytesQueued;
    private long _itemStartPosition;
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
                EnsureInitialized();

                if (chunk.Data.Length > _buffer!.BufferLength)
                {
                    throw new InvalidDataException("Assistant audio chunk exceeds the playback buffer limit.");
                }

                if (_buffer.BufferedBytes + chunk.Data.Length <= _buffer.BufferLength)
                {
                    if (_itemId is not null &&
                        !string.Equals(_itemId, chunk.ItemId, StringComparison.Ordinal))
                    {
                        TryStopSpeaker();
                        _buffer.ClearBuffer();
                        _bytesQueued = 0;
                    }

                    if (_itemId is null ||
                        !string.Equals(_itemId, chunk.ItemId, StringComparison.Ordinal))
                    {
                        _itemStartPosition = _speaker!.GetPosition();
                    }

                    _itemId = chunk.ItemId;
                    _contentIndex = chunk.ContentIndex;
                    _buffer.AddSamples(chunk.Data, 0, chunk.Data.Length);
                    _bytesQueued += chunk.Data.Length;

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

    public ValueTask<PlaybackCursor?> InterruptAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (_buffer is null || _itemId is null)
            {
                return ValueTask.FromResult<PlaybackCursor?>(null);
            }

            long playedBytes;
            try
            {
                playedBytes = Math.Clamp(
                    _speaker!.GetPosition() - _itemStartPosition,
                    0,
                    _bytesQueued);
            }
            catch (MmException exception)
            {
                WindowsAudioLog.PlaybackOperationFailed(_logger, exception.GetType().Name);
                playedBytes = Math.Clamp(
                    _bytesQueued - _buffer.BufferedBytes,
                    0,
                    _bytesQueued);
            }
            TimeSpan playedDuration = TimeSpan.FromSeconds(
                (double)playedBytes / Format.BytesPerSecond);
            PlaybackCursor cursor = new(_itemId, _contentIndex, playedDuration);

            TryStopSpeaker();
            _buffer.ClearBuffer();
            _itemId = null;
            _contentIndex = 0;
            _bytesQueued = 0;
            _itemStartPosition = 0;
            return ValueTask.FromResult<PlaybackCursor?>(cursor);
        }
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            _buffer?.ClearBuffer();
            TryStopSpeaker();
            _itemId = null;
            _contentIndex = 0;
            _bytesQueued = 0;
            _itemStartPosition = 0;
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
