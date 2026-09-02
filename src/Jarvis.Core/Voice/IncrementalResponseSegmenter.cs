using System.Text;

namespace Jarvis.Core.Voice;

/// <summary>
/// Converts generated token chunks into short, plain-text, speech-safe segments.
/// </summary>
public sealed class IncrementalResponseSegmenter
{
    private static readonly string[] Markers =
    [
        "```",
        "<think>",
        "<analysis>",
        "</think>",
        "</analysis>",
        "<tool_call>",
        "</tool_call>",
        "<function_call>",
        "</function_call>",
    ];
    private readonly StringBuilder _pending = new();
    private readonly ResponseSegmentationConfiguration _configuration;
    private string _markerCarry = string.Empty;
    private bool _inCodeFence;
    private bool _inHiddenReasoning;
    private bool _inHiddenMetadata;

    public IncrementalResponseSegmenter(
        ResponseSegmentationConfiguration? configuration = null)
    {
        _configuration = configuration ?? new ResponseSegmentationConfiguration();
    }

    public IReadOnlyList<string> Append(string tokenText)
    {
        ArgumentNullException.ThrowIfNull(tokenText);
        if (tokenText.Length == 0)
        {
            return [];
        }

        AppendVisibleText(tokenText, flush: false);
        return ExtractSegments(flush: false);
    }

    public IReadOnlyList<string> Complete()
    {
        AppendVisibleText(string.Empty, flush: true);
        return ExtractSegments(flush: true);
    }

    private void AppendVisibleText(string text, bool flush)
    {
        string combined = string.Concat(_markerCarry, text);
        int markerCarryLength = GetMarkerCarryLength(combined);
        int processLimit = combined.Length - markerCarryLength;
        int index = 0;
        while (index < processLimit)
        {
            if (TryConsumeMarker(combined, ref index, "```", ref _inCodeFence))
            {
                continue;
            }

            if (TryConsumeHiddenMarker(combined, ref index, "<think>", entering: true) ||
                TryConsumeHiddenMarker(combined, ref index, "<analysis>", entering: true) ||
                TryConsumeHiddenMarker(combined, ref index, "</think>", entering: false) ||
                TryConsumeHiddenMarker(combined, ref index, "</analysis>", entering: false))
            {
                continue;
            }

            if (TryConsumeMetadataMarker(combined, ref index, "<tool_call>", entering: true) ||
                TryConsumeMetadataMarker(combined, ref index, "<function_call>", entering: true) ||
                TryConsumeMetadataMarker(combined, ref index, "</tool_call>", entering: false) ||
                TryConsumeMetadataMarker(combined, ref index, "</function_call>", entering: false))
            {
                continue;
            }

            char character = combined[index++];
            if (!_inCodeFence && !_inHiddenReasoning && !_inHiddenMetadata && character != '`')
            {
                _pending.Append(character);
            }
        }

        _markerCarry = flush ? string.Empty : combined[index..];
    }

    private static int GetMarkerCarryLength(string text)
    {
        int longest = 0;
        foreach (string marker in Markers)
        {
            if (text.AsSpan().EndsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int maximum = Math.Min(text.Length, marker.Length - 1);
            for (int length = maximum; length > longest; length--)
            {
                if (text.AsSpan(text.Length - length).Equals(
                    marker.AsSpan(0, length),
                    StringComparison.OrdinalIgnoreCase))
                {
                    longest = length;
                    break;
                }
            }
        }

        return longest;
    }

    private List<string> ExtractSegments(bool flush)
    {
        List<string> segments = [];
        while (TryFindBoundary(flush, out int boundary))
        {
            string candidate = _pending.ToString(0, boundary);
            _pending.Remove(0, boundary);
            if (LooksLikeToolMetadata(candidate))
            {
                continue;
            }

            string sanitized = Sanitize(candidate);
            if (!string.IsNullOrWhiteSpace(sanitized) && !LooksLikeToolMetadata(sanitized))
            {
                segments.Add(sanitized);
            }
        }

        return segments;
    }

    private bool TryFindBoundary(bool flush, out int boundary)
    {
        for (int index = 0; index < _pending.Length; index++)
        {
            int length = index + 1;
            char character = _pending[index];
            if (length >= _configuration.MinimumSentenceCharacters && character is '.' or '!' or '?' or ';' ||
                length >= _configuration.MinimumClauseCharacters && character is ',' or ':' ||
                length >= _configuration.MaximumSegmentCharacters && char.IsWhiteSpace(character))
            {
                boundary = length;
                return true;
            }
        }

        if (flush && _pending.Length > 0)
        {
            boundary = _pending.Length;
            return true;
        }

        boundary = 0;
        return false;
    }

    private static string Sanitize(string text)
    {
        StringBuilder result = new(text.Length);
        bool atLineStart = true;
        foreach (char character in text)
        {
            if (character == '\0' || (char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
            {
                continue;
            }

            if (atLineStart && character is '#' or '>' or '-' or '*')
            {
                continue;
            }

            if (character is '*' or '_' or '[' or ']' or '{' or '}')
            {
                continue;
            }

            if (character is '\r' or '\n' or '\t')
            {
                if (result.Length > 0 && !char.IsWhiteSpace(result[^1]))
                {
                    result.Append(' ');
                }

                atLineStart = true;
                continue;
            }

            atLineStart = false;
            result.Append(character);
        }

        return string.Join(' ', result.ToString().Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static bool LooksLikeToolMetadata(string text)
    {
        string trimmed = text.TrimStart();
        return trimmed.StartsWith("tool:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("arguments:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("function:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("\"tool_calls\"", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("\"arguments\"", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("\"function\"", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("\"parameters\"", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("\"command\"", StringComparison.OrdinalIgnoreCase) ||
            (trimmed.StartsWith('{') && trimmed.Contains("\":", StringComparison.Ordinal));
    }

    private bool TryConsumeHiddenMarker(
        string text,
        ref int index,
        string marker,
        bool entering)
    {
        if (!text.AsSpan(index).StartsWith(marker, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _inHiddenReasoning = entering;
        index += marker.Length;
        return true;
    }

    private bool TryConsumeMetadataMarker(
        string text,
        ref int index,
        string marker,
        bool entering)
    {
        if (!text.AsSpan(index).StartsWith(marker, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _inHiddenMetadata = entering;
        index += marker.Length;
        return true;
    }

    private static bool TryConsumeMarker(
        string text,
        ref int index,
        string marker,
        ref bool state)
    {
        if (!text.AsSpan(index).StartsWith(marker, StringComparison.Ordinal))
        {
            return false;
        }

        state = !state;
        index += marker.Length;
        return true;
    }
}
