using Jarvis.Core.Voice;
using Jarvis.Infrastructure.Configuration;

namespace Jarvis.Infrastructure.Voice.Local;

internal sealed class LocalAssetPaths(JarvisDataPaths paths)
{
    public const string SupportedLanguageModelId = "qwen3-4b-q4-k-m";
    public const string SupportedSpeechProfile = "zipformer-en-20m-int8";
    public const string SupportedWakeWordProfile =
        "sherpa-onnx-kws-zipformer-gigaspeech-3.3M-2024-01-01";

    public string LlamaServerExecutable =>
        JarvisDataPaths.ResolveUnder(paths.LlamaCppRuntime, "llama-server.exe");

    public string LanguageModel =>
        JarvisDataPaths.ResolveUnder(paths.LlmModels, "Qwen3-4B-Q4_K_M.gguf");

    public string VadModel =>
        JarvisDataPaths.ResolveUnder(paths.VadModels, "silero_vad.onnx");

    public string SpeechModelDirectory =>
        JarvisDataPaths.ResolveUnder(
            paths.SpeechModels,
            "sherpa-onnx-streaming-zipformer-en-20M-2023-02-17");

    public string SpeechEncoder => Path.Combine(
        SpeechModelDirectory,
        "encoder-epoch-99-avg-1.int8.onnx");

    public string SpeechDecoder => Path.Combine(
        SpeechModelDirectory,
        "decoder-epoch-99-avg-1.onnx");

    public string SpeechJoiner => Path.Combine(
        SpeechModelDirectory,
        "joiner-epoch-99-avg-1.int8.onnx");

    public string SpeechTokens => Path.Combine(SpeechModelDirectory, "tokens.txt");

    public string WakeWordModelDirectory =>
        JarvisDataPaths.ResolveUnder(paths.WakeWordModels, SupportedWakeWordProfile);

    public string WakeWordEncoder => Path.Combine(
        WakeWordModelDirectory,
        "encoder-epoch-12-avg-2-chunk-16-left-64.int8.onnx");

    public string WakeWordDecoder => Path.Combine(
        WakeWordModelDirectory,
        "decoder-epoch-12-avg-2-chunk-16-left-64.int8.onnx");

    public string WakeWordJoiner => Path.Combine(
        WakeWordModelDirectory,
        "joiner-epoch-12-avg-2-chunk-16-left-64.int8.onnx");

    public string WakeWordTokens => Path.Combine(WakeWordModelDirectory, "tokens.txt");

    public string TtsModelDirectory =>
        JarvisDataPaths.ResolveUnder(paths.TtsModels, "kokoro-en-v0_19");

    public string TtsModel => Path.Combine(TtsModelDirectory, "model.onnx");

    public string TtsVoices => Path.Combine(TtsModelDirectory, "voices.bin");

    public string TtsTokens => Path.Combine(TtsModelDirectory, "tokens.txt");

    public string TtsDataDirectory => Path.Combine(TtsModelDirectory, "espeak-ng-data");

    public static LocalComponentUnavailableException Missing(string component) =>
        new(
            $"{component}_not_installed",
            "Local model not installed. Run scripts/setup-local-ai.ps1 from the repository root.");
}
