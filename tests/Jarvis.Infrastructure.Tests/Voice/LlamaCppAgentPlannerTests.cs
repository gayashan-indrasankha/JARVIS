using System.Net;
using System.Text.Json;
using Jarvis.Core.Tools;
using Jarvis.Core.Voice;
using Jarvis.Infrastructure.Voice.Local.Llama;

namespace Jarvis.Infrastructure.Tests.Voice;

public sealed class LlamaCppAgentPlannerTests
{
    [Fact]
    public async Task SchemaConstrainedResponseProducesBoundedToolProposalWithoutRegisteringServerTools()
    {
        RecordingHandler handler = new(Response(new
        {
            action = "tool",
            tool = "get_system_metrics",
            arguments = new { },
        }));
        await using LlamaCppAgentPlanner planner = new(
            new FakeSupervisor(),
            new FakeHttpClientFactory(handler));

        AgentPlan plan = await planner.PlanAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(AgentPlanKind.ToolCall, plan.Kind);
        Assert.Equal("get_system_metrics", plan.ToolCall?.Name);
        Assert.Equal("{}", plan.ToolCall?.ArgumentsJson);
        string body = Assert.Single(handler.RequestBodies);
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        Assert.False(root.TryGetProperty("tools", out _));
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal(
            "json_schema",
            root.GetProperty("response_format").GetProperty("type").GetString());
        Assert.True(
            root.GetProperty("response_format").GetProperty("json_schema")
                .GetProperty("strict").GetBoolean());
        string serialized = root.GetRawText();
        Assert.Contains("get_system_metrics", serialized, StringComparison.Ordinal);
        Assert.Contains("cannot override", serialized, StringComparison.Ordinal);
        Assert.Contains("C:\\\\safe", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneRepairAttemptRecoversMalformedPlannerOutput()
    {
        RecordingHandler handler = new(
            ResponseContent("not-json"),
            Response(new { action = "respond" }));
        await using LlamaCppAgentPlanner planner = new(
            new FakeSupervisor(),
            new FakeHttpClientFactory(handler));

        AgentPlan plan = await planner.PlanAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(AgentPlanKind.Respond, plan.Kind);
        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.Contains(
            "previous structured output was invalid",
            handler.RequestBodies[1],
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SecondMalformedOutputReturnsInvalidAndNeverInventsCall()
    {
        RecordingHandler handler = new(
            ResponseContent("{\"action\":\"tool\",\"tool\":\"get_system_metrics\"}"),
            ResponseContent("{\"action\":\"tool\",\"arguments\":{}}"));
        await using LlamaCppAgentPlanner planner = new(
            new FakeSupervisor(),
            new FakeHttpClientFactory(handler));

        AgentPlan plan = await planner.PlanAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(AgentPlanKind.Invalid, plan.Kind);
        Assert.Null(plan.ToolCall);
        Assert.Equal(2, handler.RequestBodies.Count);
    }

    [Fact]
    public async Task PlannerHttpFailureUsesStableContentFreeError()
    {
        await using LlamaCppAgentPlanner planner = new(
            new FakeSupervisor(),
            new FakeHttpClientFactory(new StatusHandler(HttpStatusCode.BadRequest)));

        LocalComponentUnavailableException exception = await Assert.ThrowsAsync<
            LocalComponentUnavailableException>(async () =>
                await planner.PlanAsync(CreateRequest(), CancellationToken.None));

        Assert.Equal("local_llm_tool_planning_failed", exception.Code);
        Assert.DoesNotContain("response", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static AgentPlanningRequest CreateRequest() => new(
        [
            new ConversationMessage(ConversationRole.System, "Test persona."),
            new ConversationMessage(ConversationRole.User, "How much RAM is used?"),
        ],
        [
            new ToolDefinition(
                "get_system_metrics",
                "Read current system metrics.",
                "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{}}",
                ToolAuthorizationCategory.SafeRead,
                TimeSpan.FromSeconds(2),
                1024),
        ],
        ["C:\\safe"]);

    private static string Response<T>(T plan) =>
        ResponseContent(JsonSerializer.Serialize(plan));

    private static string ResponseContent(string content) => JsonSerializer.Serialize(new
    {
        choices = new[]
        {
            new
            {
                message = new { content },
            },
        },
    });

    private sealed class FakeSupervisor : ILlamaServerSupervisor
    {
        public ValueTask<LlamaServerConnection> EnsureReadyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new LlamaServerConnection(
                new Uri("http://127.0.0.1:18080/"),
                "test-token",
                8192));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) :
        ILoopbackHttpClientFactory
    {
        public HttpClient Create(Uri endpoint, string? authenticationToken)
        {
            _ = authenticationToken;
            return new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = endpoint,
                Timeout = Timeout.InfiniteTimeSpan,
            };
        }
    }

    private sealed class RecordingHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue()),
            };
        }
    }

    private sealed class StatusHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }
    }
}
