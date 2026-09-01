using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Jarvis.Core.Voice;
using Jarvis.Infrastructure.Configuration;
using Microsoft.Extensions.Options;
using SherpaOnnx;

namespace Jarvis.Infrastructure.Voice.Local.Sherpa;

internal sealed class SherpaOnnxKokoroSpeechSynthesizer : ISpeechSynthesizer
{
    private const int MaximumCallbackChunkBytes = 64 * 1024;
    private static readonly Dictionary<string, int> SpeakerIds =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["af"] = 0,
            ["af_bella"] = 1,
            ["af_nicole"] = 2,
            ["af_sarah"] = 3,
            ["af_sky"] = 4,
            ["am_adam"] = 5,
            ["am_michael"] = 6,
            ["bf_emma"] = 7,
            ["bf_isabella"] = 8,
            ["bm_george"] = 9,
            ["bm_lewis"] = 10,
        };
    private readonly LocalAssetPaths _assets;
    private readonly VoiceOptions _options;
    private readonly IVoiceMetrics _metrics;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OfflineTts? _tts;
    private bool _disposed;

    public SherpaOnnxKokoroSpeechSynthesizer(
        LocalAssetPaths assets,
        IOptions<VoiceOptions> options,
        IVoiceMetrics metrics)
    {
        _assets = assets;
        _options = options.Value;
        _metrics = metrics;
    }

    public AudioFormat OutputFormat => AudioFormat.Pcm16Mono24Khz;

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_tts is not null)
            {
                return;
            }

            ValidateAssetsAndVoice();
            OfflineTtsConfig configuration = new();
            configuration.Model.Kokoro.Model = _assets.TtsModel;
            configuration.Model.Kokoro.Voices = _assets.TtsVoices;
            configuration.Model.Kokoro.Tokens = _assets.TtsTokens;
            configuration.Model.Kokoro.DataDir = _assets.TtsDataDirectory;
            configuration.Model.NumThreads = 2;
            configuration.Model.Provider = "cpu";
            configuration.Model.Debug = 0;
            configuration.MaxNumSentences = 1;

            OfflineTts created = await Task.Run(() => new OfflineTts(configuration))
                .ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                created.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (created.SampleRate != OutputFormat.SampleRateHz)
            {
                created.Dispose();
                throw new InvalidDataException("The Kokoro model has an unexpected sample rate.");
            }

            _tts = created;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async IAsyncEnumerable<SynthesizedAudioChunk> SynthesizeAsync(
        SpeechSynthesisRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using CancellationTokenSource synthesisCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Channel<SynthesizedAudioChunk> chunks = Channel.CreateBounded<SynthesizedAudioChunk>(
            new BoundedChannelOptions(8)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
            });
        Task producer = Task.Run(
            () => ProduceAudio(request, chunks.Writer, synthesisCancellation.Token),
            CancellationToken.None);

        try
        {
            await foreach (SynthesizedAudioChunk chunk in
                chunks.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return chunk;
            }

            await producer.ConfigureAwait(false);
        }
        finally
        {
            synthesisCancellation.Cancel();
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (synthesisCancellation.IsCancellationRequested)
            {
            }

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
            _tts?.Dispose();
            _tts = null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private void ProduceAudio(
        SpeechSynthesisRequest request,
        ChannelWriter<SynthesizedAudioChunk> writer,
        CancellationToken cancellationToken)
    {
        Stopwatch firstAudio = Stopwatch.StartNew();
        int firstAudioRecorded = 0;
        OfflineTtsGenerationConfig generation = new()
        {
            Sid = SpeakerIds[_options.TtsVoice],
            Speed = _options.TtsSpeed,
            SilenceScale = 0.2F,
        };

        try
        {
            OfflineTtsGeneratedAudio? generatedAudio = null;
            try
            {
                generatedAudio = _tts!.GenerateWithConfig(
                    request.Text,
                    generation,
                    (samplesPointer, sampleCount, _, _) =>
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            return 0;
                        }

                        float[] samples = new float[sampleCount];
                        Marshal.Copy(samplesPointer, samples, 0, sampleCount);
                        byte[] pcm = PcmAudio.FloatToPcm16(samples);
                        for (int offset = 0; offset < pcm.Length; offset += MaximumCallbackChunkBytes)
                        {
                            int count = Math.Min(MaximumCallbackChunkBytes, pcm.Length - offset);
                            byte[] ownedChunk = new byte[count];
                            Buffer.BlockCopy(pcm, offset, ownedChunk, 0, count);
                            try
                            {
                                writer.WriteAsync(
                                        new SynthesizedAudioChunk(ownedChunk),
                                        cancellationToken)
                                    .AsTask()
                                    .GetAwaiter()
                                    .GetResult();
                            }
                            catch (OperationCanceledException)
                            {
                                return 0;
                            }

                            if (Interlocked.Exchange(ref firstAudioRecorded, 1) == 0)
                            {
                                _metrics.Record(new VoiceMetric(
                                    VoiceMetricKind.TextToSpeechFirstAudio,
                                    firstAudio.Elapsed.TotalMilliseconds));
                            }
                        }

                        return cancellationToken.IsCancellationRequested ? 0 : 1;
                    });
            }
            finally
            {
                generatedAudio?.Dispose();
            }

            cancellationToken.ThrowIfCancellationRequested();
            writer.TryComplete();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            writer.TryComplete();
        }
        catch (Exception exception)
        {
            writer.TryComplete(exception);
        }
    }

    private void ValidateAssetsAndVoice()
    {
        if (!SpeakerIds.ContainsKey(_options.TtsVoice))
        {
            throw new LocalComponentUnavailableException(
                "tts_voice_unsupported",
                "The configured Voice:TtsVoice is not present in the tracked model manifest.");
        }

        if (!File.Exists(_assets.TtsModel) ||
            !File.Exists(_assets.TtsVoices) ||
            !File.Exists(_assets.TtsTokens) ||
            !Directory.Exists(_assets.TtsDataDirectory))
        {
            throw LocalAssetPaths.Missing("tts_model");
        }
    }
}
