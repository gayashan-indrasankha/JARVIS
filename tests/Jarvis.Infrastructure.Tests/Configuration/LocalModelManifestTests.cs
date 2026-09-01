using System.Text.Json;

namespace Jarvis.Infrastructure.Tests.Configuration;

public sealed class LocalModelManifestTests
{
    [Fact]
    public void ManifestPinsRequiredLocalComponentsAndValidChecksums()
    {
        string manifestPath = Path.Combine(
            AppContext.BaseDirectory,
            "config",
            "local-model-manifest.json");
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement root = manifest.RootElement;

        Assert.Equal(1, root.GetProperty("manifestVersion").GetInt32());
        Assert.Equal("b10708", root.GetProperty("llamaCpp").GetProperty("version").GetString());
        JsonElement models = root.GetProperty("models");
        string[] logicalIds = models.EnumerateArray()
            .Select(static model => model.GetProperty("logicalId").GetString()!)
            .ToArray();
        Assert.Contains("qwen3-4b-q4-k-m", logicalIds, StringComparer.Ordinal);
        Assert.Contains("silero-vad-v4", logicalIds, StringComparer.Ordinal);
        Assert.Contains("zipformer-en-20m-int8", logicalIds, StringComparer.Ordinal);
        Assert.Contains("zipformer-gigaspeech-kws-3.3m-int8", logicalIds, StringComparer.Ordinal);
        Assert.Contains("kokoro-en-v0-19-bm-george", logicalIds, StringComparer.Ordinal);

        IEnumerable<string> checksums = root.GetProperty("llamaCpp")
            .GetProperty("variants")
            .EnumerateObject()
            .SelectMany(static variant => variant.Value.EnumerateObject())
            .Where(static property => property.Name.Contains("sha256", StringComparison.OrdinalIgnoreCase))
            .Select(static property => property.Value.GetString()!)
            .Concat(models.EnumerateArray()
                .Where(static model => model.GetProperty("sha256").ValueKind == JsonValueKind.String)
                .Select(static model => model.GetProperty("sha256").GetString()!));

        Assert.All(checksums, static checksum =>
        {
            Assert.Equal(64, checksum.Length);
            Assert.All(checksum, static character =>
                Assert.True(char.IsAsciiHexDigit(character) && !char.IsUpper(character)));
        });
    }

    [Fact]
    public void ManifestUsesOnlyApprovedHttpsOriginsAndDocumentsEveryLicense()
    {
        using JsonDocument manifest = LoadManifest();
        JsonElement root = manifest.RootElement;
        List<string> urls = [];
        foreach (JsonProperty variant in root.GetProperty("llamaCpp")
            .GetProperty("variants")
            .EnumerateObject())
        {
            urls.Add(variant.Value.GetProperty("url").GetString()!);
            if (variant.Value.TryGetProperty("cudaRuntimeUrl", out JsonElement cudaUrl))
            {
                urls.Add(cudaUrl.GetString()!);
            }
        }

        foreach (JsonElement model in root.GetProperty("models").EnumerateArray())
        {
            urls.Add(model.GetProperty("url").GetString()!);
            Assert.False(string.IsNullOrWhiteSpace(model.GetProperty("license").GetString()));
        }

        Assert.All(urls, static value =>
        {
            Uri uri = new(value, UriKind.Absolute);
            Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
            Assert.Contains(uri.Host, ["github.com", "huggingface.co"], StringComparer.Ordinal);
            Assert.Empty(uri.UserInfo);
        });
    }

    [Fact]
    public void OnlyDocumentedZipformerArchiveLacksAuthoritativeChecksum()
    {
        using JsonDocument manifest = LoadManifest();
        string[] missingChecksums = manifest.RootElement.GetProperty("models")
            .EnumerateArray()
            .Where(static model => model.GetProperty("sha256").ValueKind == JsonValueKind.Null)
            .Select(static model => model.GetProperty("logicalId").GetString()!)
            .ToArray();

        Assert.Equal(["zipformer-en-20m-int8"], missingChecksums);
    }

    [Fact]
    public void LicenseProvenanceIsAssociatedWithTheCorrectModels()
    {
        using JsonDocument manifest = LoadManifest();
        Dictionary<string, string> licenses = manifest.RootElement.GetProperty("models")
            .EnumerateArray()
            .ToDictionary(
                static model => model.GetProperty("logicalId").GetString()!,
                static model => model.GetProperty("license").GetString()!,
                StringComparer.Ordinal);

        Assert.Equal("Apache-2.0", licenses["qwen3-4b-q4-k-m"]);
        Assert.Contains("LibriSpeech", licenses["zipformer-en-20m-int8"], StringComparison.Ordinal);
        Assert.Contains(
            "GigaSpeech XL",
            licenses["zipformer-gigaspeech-kws-3.3m-int8"],
            StringComparison.Ordinal);
        Assert.DoesNotContain("LibriSpeech", licenses["qwen3-4b-q4-k-m"], StringComparison.Ordinal);
    }

    [Fact]
    public void WakeWordModelPinsJarvisTokenizationAndArchiveIdentity()
    {
        using JsonDocument manifest = LoadManifest();
        JsonElement model = manifest.RootElement.GetProperty("models")
            .EnumerateArray()
            .Single(static candidate => string.Equals(
                candidate.GetProperty("logicalId").GetString(),
                "zipformer-gigaspeech-kws-3.3m-int8",
                StringComparison.Ordinal));

        Assert.Equal("keyword-spotting", model.GetProperty("kind").GetString());
        Assert.Equal("▁JA R VI S @JARVIS", model.GetProperty("keywordTokens").GetString());
        Assert.Equal(17_626_723, model.GetProperty("expectedBytes").GetInt64());
        Assert.Equal(
            "f170013b4716e41b62b9bfd809687c207cef798ef9bc6534d524e17af9b6561a",
            model.GetProperty("sha256").GetString());
    }

    private static JsonDocument LoadManifest()
    {
        string manifestPath = Path.Combine(
            AppContext.BaseDirectory,
            "config",
            "local-model-manifest.json");
        return JsonDocument.Parse(File.ReadAllText(manifestPath));
    }
}
