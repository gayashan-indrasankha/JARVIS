using Jarvis.Core.Voice;
using Jarvis.Infrastructure.Configuration;
using Jarvis.Infrastructure.Voice.Local;
using Jarvis.Infrastructure.Voice.Local.Sherpa;
using Microsoft.Extensions.Options;

namespace Jarvis.Infrastructure.Tests.Voice;

public sealed class LocalSpeechAssetValidationTests
{
    [Theory]
    [InlineData("vad_model_not_installed")]
    [InlineData("speech_recognition_model_not_installed")]
    [InlineData("tts_model_not_installed")]
    [InlineData("wake_word_model_not_installed")]
    public async Task MissingModelsFailBeforeLoadingNativeRuntime(string expectedCode)
    {
        string home = Path.Combine(Path.GetTempPath(), $"jarvis-speech-tests-{Guid.NewGuid():N}");
        LocalAssetPaths assets = new(JarvisDataPaths.Create(home));
        IAsyncDisposable component = expectedCode switch
        {
            "vad_model_not_installed" => new SherpaOnnxVoiceActivityDetector(
                assets,
                Options.Create(new VoiceOptions())),
            "speech_recognition_model_not_installed" => new SherpaOnnxSpeechRecognizer(
                assets,
                Options.Create(new VoiceOptions())),
            "wake_word_model_not_installed" => new SherpaOnnxKeywordSpotter(
                assets,
                new NullCapture(),
                Options.Create(new VoiceOptions()),
                TimeProvider.System),
            _ => new SherpaOnnxKokoroSpeechSynthesizer(
                assets,
                Options.Create(new VoiceOptions()),
                new NullMetrics()),
        };
        try
        {
            LocalComponentUnavailableException exception = await Assert.ThrowsAsync<
                LocalComponentUnavailableException>(async () =>
            {
                switch (component)
                {
                    case IVoiceActivityDetector detector:
                        await detector.InitializeAsync(CancellationToken.None);
                        break;
                    case ISpeechRecognizer recognizer:
                        await recognizer.InitializeAsync(CancellationToken.None);
                        break;
                    case ISpeechSynthesizer synthesizer:
                        await synthesizer.InitializeAsync(CancellationToken.None);
                        break;
                    case IWakeWordDetector wakeWordDetector:
                        await foreach (WakeWordDetection detection in
                            wakeWordDetector.ListenAsync(CancellationToken.None))
                        {
                            _ = detection;
                        }

                        break;
                }
            });

            Assert.Equal(expectedCode, exception.Code);
            Assert.Contains("setup-local-ai.ps1", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            await component.DisposeAsync();
        }
    }

    [Fact]
    public void JarvisPhraseUsesPinnedGigaspeechBpeTokens()
    {
        Assert.Equal("▁JA R VI S @JARVIS", SherpaOnnxKeywordSpotter.JarvisKeywordTokens);
    }

    private sealed class NullMetrics : IVoiceMetrics
    {
        public void Record(VoiceMetric metric) => _ = metric;
    }

    private sealed class NullCapture : IAudioCapture
    {
        public AudioFormat Format => AudioFormat.Pcm16Mono16Khz;

        public async IAsyncEnumerable<AudioFrame> CaptureAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
