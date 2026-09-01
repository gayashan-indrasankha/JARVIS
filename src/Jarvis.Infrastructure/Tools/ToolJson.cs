using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jarvis.Infrastructure.Tools;

internal static class ToolJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false),
        },
    };

    public static void ValidateUnambiguousObject(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                HasDuplicateProperties(document.RootElement))
            {
                throw new ToolValidationException("malformed_arguments_json");
            }
        }
        catch (JsonException)
        {
            throw new ToolValidationException("malformed_arguments_json");
        }
    }

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().Any(HasDuplicateProperties);
        }

        return false;
    }
}

internal static class InitialToolSchemas
{
    public const string ListDirectory =
        """{"type":"object","additionalProperties":false,"required":["path"],"properties":{"path":{"type":"string","minLength":1,"maxLength":2048},"maximumEntries":{"type":"integer","minimum":1,"maximum":256,"default":100}}}""";

    public const string FindFiles =
        """{"type":"object","additionalProperties":false,"required":["path","pattern"],"properties":{"path":{"type":"string","minLength":1,"maxLength":2048},"pattern":{"type":"string","minLength":1,"maxLength":128},"recursive":{"type":"boolean","default":true},"maximumResults":{"type":"integer","minimum":1,"maximum":256,"default":100}}}""";

    public const string PathOnly =
        """{"type":"object","additionalProperties":false,"required":["path"],"properties":{"path":{"type":"string","minLength":1,"maxLength":2048}}}""";

    public const string ReadTextFile =
        """{"type":"object","additionalProperties":false,"required":["path"],"properties":{"path":{"type":"string","minLength":1,"maxLength":2048},"maximumCharacters":{"type":"integer","minimum":256,"maximum":32768,"default":16384}}}""";

    public const string LaunchApplication =
        """{"type":"object","additionalProperties":false,"required":["application"],"properties":{"application":{"type":"string","enum":["notepad","calculator","paint"]}}}""";

    public const string ListProcesses =
        """{"type":"object","additionalProperties":false,"properties":{"maximumResults":{"type":"integer","minimum":1,"maximum":256,"default":100}}}""";

    public const string Empty =
        """{"type":"object","additionalProperties":false,"properties":{}}""";

    public const string GetGitStatus =
        """{"type":"object","additionalProperties":false,"required":["repositoryPath"],"properties":{"repositoryPath":{"type":"string","minLength":1,"maxLength":2048}}}""";

    public const string ExecuteSafeCommand =
        """{"type":"object","additionalProperties":false,"required":["command"],"properties":{"command":{"type":"string","enum":["dotnet_info","dotnet_version","git_version"]}}}""";
}
