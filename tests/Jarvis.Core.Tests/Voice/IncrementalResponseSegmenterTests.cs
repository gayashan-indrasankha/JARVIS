using Jarvis.Core.Voice;

namespace Jarvis.Core.Tests.Voice;

public sealed class IncrementalResponseSegmenterTests
{
    [Fact]
    public void EmitsSentenceAndClauseSegmentsWithinBounds()
    {
        IncrementalResponseSegmenter segmenter = new();

        IReadOnlyList<string> first = segmenter.Append(
            "This is a sufficiently long first sentence. This second clause is deliberately long, ");
        IReadOnlyList<string> final = segmenter.Complete();

        Assert.Equal("This is a sufficiently long first sentence.", Assert.Single(first));
        Assert.Equal("This second clause is deliberately long,", Assert.Single(final));
        Assert.All(first.Concat(final), static value =>
            Assert.InRange(value.Length, 1, VoiceDataLimits.MaximumSpeechSegmentCharacters));
    }

    [Fact]
    public void RemovesHiddenReasoningCodeAndToolMetadataAcrossTokenBoundaries()
    {
        IncrementalResponseSegmenter segmenter = new();
        List<string> output = [];

        output.AddRange(segmenter.Append("<thi"));
        output.AddRange(segmenter.Append("nk>private chain of thought</think>```csharp\nDanger();\n```"));
        output.AddRange(segmenter.Append(
            "tool: execute arguments: secret. The safe spoken answer is available now."));
        output.AddRange(segmenter.Complete());

        string combined = string.Join(' ', output);
        Assert.DoesNotContain("private", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Danger", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("arguments", combined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("safe spoken answer", combined, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemovesSplitToolCallTagsAndJsonPayloads()
    {
        IncrementalResponseSegmenter segmenter = new();
        List<string> output = [];

        output.AddRange(segmenter.Append("<tool_"));
        output.AddRange(segmenter.Append("call>{\"command\":\"Danger\"}</tool_call>"));
        output.AddRange(segmenter.Append("{\"function\":\"run\",\"arguments\":{}}. "));
        output.AddRange(segmenter.Append("A safe visible sentence follows this metadata."));
        output.AddRange(segmenter.Complete());

        string combined = string.Join(' ', output);
        Assert.DoesNotContain("Danger", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("function", combined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("safe visible sentence", combined, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DropsIncompleteInternalMarkerAtEndOfGeneration()
    {
        IncrementalResponseSegmenter segmenter = new();

        List<string> segments = [.. segmenter.Append("A safe visible sentence is complete. <thi")];
        segments.AddRange(segmenter.Complete());
        string spoken = string.Join(' ', segments);

        Assert.Contains("safe visible sentence", spoken, StringComparison.Ordinal);
        Assert.DoesNotContain("<thi", spoken, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UsesConfiguredBoundaries()
    {
        IncrementalResponseSegmenter segmenter = new(
            new ResponseSegmentationConfiguration(
                minimumSentenceCharacters: 8,
                minimumClauseCharacters: 12,
                maximumSegmentCharacters: 20));

        IReadOnlyList<string> output = segmenter.Append("A short sentence. Trailing text");

        Assert.Equal("A short sentence.", Assert.Single(output));
    }
}
