using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Jarvis.Core.Voice;
using Jarvis.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NAudio;
using NAudio.Wave;

namespace Jarvis.Infrastructure.Voice;

internal sealed class WindowsMicrophoneCapture : IAudioCapture
{
    private readonly AudioDeviceOptions _options;
    private readonly ILogger<WindowsMicrophoneCapture> _logger;
    private int _active;
    private int _disposed;

    public WindowsMicrophoneCapture(
        IOptions<VoiceOptions> options,
        ILogger<WindowsMicrophoneCapture> logger)
    {
        _options = options.Value.Audio;
        _logger = logger;
    }

    public AudioFormat Format => AudioFormat.Pcm16Mono24Khz;

    public async IAsyncEnumerable<AudioFrame> CaptureAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
        {
            throw new InvalidOperationException("Microphone capture is already active.");
        }

        Channel<AudioFrame> frames = Channel.CreateBounded<AudioFrame>(
            new BoundedChannelOptions(32)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
            });
        int droppedFrames = 0;

        using WaveIn microphone = new()
        {
            DeviceNumber = _options.InputDeviceNumber,
            BufferMilliseconds = _options.CaptureBufferMilliseconds,
            NumberOfBuffers = 3,
            WaveFormat = new WaveFormat(
                Format.SampleRateHz,
                Format.BitsPerSample,
                Format.ChannelCount),
        };

        microphone.DataAvailable += (_, eventArgs) =>
        {
            if (eventArgs.BufferSpan.Length > VoiceDataLimits.MaximumAudioChunkBytes)
            {
                Interlocked.Increment(ref droppedFrames);
                return;
            }

            byte[] data = eventArgs.BufferSpan.ToArray();
            if (data.Length > 0 && !frames.Writer.TryWrite(new AudioFrame(data)))
            {
                Interlocked.Increment(ref droppedFrames);
            }
        };
        microphone.RecordingStopped += (_, eventArgs) =>
        {
            if (eventArgs.Exception is null)
            {
                frames.Writer.TryComplete();
            }
            else
            {
                frames.Writer.TryComplete(eventArgs.Exception);
            }
        };

        try
        {
            using CancellationTokenRegistration registration =
                cancellationToken.Register(() => TryStopMicrophone(microphone));
            microphone.StartRecording();
            WindowsAudioLog.CaptureStarted(
                _logger,
                Format.SampleRateHz,
                Format.ChannelCount,
                Format.BitsPerSample);

            await foreach (AudioFrame frame in
                frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return frame;
            }
        }
        finally
        {
            TryStopMicrophone(microphone);

            Interlocked.Exchange(ref _active, 0);
            int finalDroppedFrames = Volatile.Read(ref droppedFrames);
            WindowsAudioLog.CaptureStopped(_logger, finalDroppedFrames);
        }
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }

    private void TryStopMicrophone(WaveIn microphone)
    {
        try
        {
            microphone.StopRecording();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or MmException)
        {
            WindowsAudioLog.CaptureStopFailed(_logger, exception.GetType().Name);
        }
    }
}

internal static partial class WindowsAudioLog
{
    [LoggerMessage(
        EventId = 2200,
        Level = LogLevel.Information,
        Message = "Microphone capture started: {SampleRateHz} Hz, {Channels} channel(s), {BitsPerSample} bits")]
    public static partial void CaptureStarted(
        ILogger logger,
        int sampleRateHz,
        int channels,
        int bitsPerSample);

    [LoggerMessage(
        EventId = 2201,
        Level = LogLevel.Information,
        Message = "Microphone capture stopped; dropped frame count {DroppedFrames}")]
    public static partial void CaptureStopped(ILogger logger, int droppedFrames);

    [LoggerMessage(
        EventId = 2202,
        Level = LogLevel.Information,
        Message = "Speaker playback initialized: {SampleRateHz} Hz, {Channels} channel(s), {BitsPerSample} bits")]
    public static partial void PlaybackStarted(
        ILogger logger,
        int sampleRateHz,
        int channels,
        int bitsPerSample);

    [LoggerMessage(
        EventId = 2203,
        Level = LogLevel.Warning,
        Message = "Microphone stop reported {ErrorType}")]
    public static partial void CaptureStopFailed(ILogger logger, string errorType);

    [LoggerMessage(
        EventId = 2204,
        Level = LogLevel.Warning,
        Message = "Speaker operation reported {ErrorType}")]
    public static partial void PlaybackOperationFailed(ILogger logger, string errorType);
}
