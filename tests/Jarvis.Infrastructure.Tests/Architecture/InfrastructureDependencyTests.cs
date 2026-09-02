using System.Reflection;
using Jarvis.Core.ProjectIntelligence;
using Jarvis.Core.Tools;
using Jarvis.Infrastructure.Configuration;
using Jarvis.Infrastructure.Voice.Local.Llama;

namespace Jarvis.Infrastructure.Tests.Architecture;

public sealed class InfrastructureDependencyTests
{
    [Fact]
    public void InfrastructureReferencesCoreButNotHost()
    {
        string[] referencedAssemblies = typeof(VoiceOptions)
            .Assembly
            .GetReferencedAssemblies()
            .Select(static assembly => assembly.Name ?? string.Empty)
            .ToArray();

        Assert.Contains("Jarvis.Core", referencedAssemblies, StringComparer.Ordinal);
        Assert.DoesNotContain("Jarvis.Host", referencedAssemblies, StringComparer.Ordinal);
        Assert.DoesNotContain(
            referencedAssemblies,
            static reference =>
                reference.StartsWith("OpenAI", StringComparison.Ordinal) ||
                reference.StartsWith("System.Net.WebSockets", StringComparison.Ordinal));
    }

    [Fact]
    public void LocalModelAdaptersHaveNoToolExecutionOrAuthorizationHandle()
    {
        Type[] adapters =
        [
            typeof(LlamaCppLocalLanguageModel),
            typeof(LlamaCppAgentPlanner),
        ];
        Type[] forbiddenContracts =
        [
            typeof(IToolDispatcher),
            typeof(IToolAuthorizationPolicy),
            typeof(IToolAuditSink),
            typeof(IProjectIntelligenceService),
        ];

        foreach (Type adapter in adapters)
        {
            FieldInfo[] fields = adapter.GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.DoesNotContain(fields, field =>
                forbiddenContracts.Any(contract => contract.IsAssignableFrom(field.FieldType)));
        }
    }
}
