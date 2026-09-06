using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Mcp;
using Nexus.Service.Mcp.Assistant;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.Mcp.Assistant;

/// <summary>
/// The agentic loop against a fake <see cref="IOllamaClient"/>: a tool_call
/// round followed by a final-answer round runs the tool THROUGH
/// McpToolRegistry (consent + audit), strips reasoning from the final
/// answer, and the round cap holds when the model never stops calling tools.
/// </summary>
public sealed class LocalAssistantTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "nexus-assistant-loop-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private TestableConfigStore NewEnabledStore(string model = "qwen3.5:4b")
    {
        var store = new TestableConfigStore(Path.Combine(_tempDir, "settings.json"));
        store.Update(s =>
        {
            s.AiIntegration.Enabled = true;
            s.AiIntegration.AssistantActiveModel = model;
        });
        return store;
    }

    /// <summary>Builds a manager already in the Running state: the fake client
    /// answers TryGetVersionAsync, so InstallRuntimeAsync adopts it as a
    /// "system Ollama" without touching the never-called downloader/process host.</summary>
    private OllamaRuntimeManager RunningManager(IOllamaClient client, TestableConfigStore store)
    {
        var manager = new OllamaRuntimeManager(
            new HttpClient(new NeverCalledHandler()),
            new NeverCalledProcessHost(),
            _ => client,
            store,
            new MultiplexHub(),
            _tempDir);
        // Adopting a listener on the default port is opt-in.
        store.Update(s => s.AiIntegration.UseSystemOllama = true);
        manager.InstallRuntimeAsync(CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal(AssistantRuntimeState.Running, manager.State);
        return manager;
    }

    [Fact]
    public async Task RunAsync_refuses_when_master_toggle_is_off()
    {
        var store = new TestableConfigStore(Path.Combine(_tempDir, "settings.json"));
        var client = new ScriptedOllamaClient();
        var manager = RunningManager(client, store);
        var registry = new McpToolRegistry(Array.Empty<IMcpTool>(), store, new NullAuditSink());
        var assistant = new LocalAssistant(registry, store, manager);

        var outcome = await assistant.RunAsync("what's my cpu temp", CancellationToken.None);

        Assert.True(outcome.IsRefused);
        Assert.Contains("turned off", outcome.RefusalReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, client.ChatCallCount);
    }

    [Fact]
    public async Task RunAsync_refuses_when_no_model_is_selected()
    {
        var store = NewEnabledStore(model: "");
        var client = new ScriptedOllamaClient();
        var manager = RunningManager(client, store);
        var registry = new McpToolRegistry(Array.Empty<IMcpTool>(), store, new NullAuditSink());
        var assistant = new LocalAssistant(registry, store, manager);

        var outcome = await assistant.RunAsync("what's my cpu temp", CancellationToken.None);

        Assert.True(outcome.IsRefused);
        Assert.Contains("model", outcome.RefusalReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_runs_a_tool_call_through_the_registry_then_returns_the_final_answer()
    {
        var store = NewEnabledStore();
        var tool = new FakeTool
        {
            Name = "get_system_overview",
            Capability = McpCapability.Telemetry,
            ReadOnly = true,
            Behavior = _ => McpToolExecutionResult.Ok("{\"cpuModel\":\"Test CPU\"}"),
        };
        var audit = new RecordingAuditSink();
        var registry = new McpToolRegistry(new IMcpTool[] { tool }, store, audit);

        var client = new ScriptedOllamaClient(
            ToolCallResponse("get_system_overview", "{}"),
            FinalAnswerResponse("<think>the tool says Test CPU</think>Your CPU is a Test CPU."));
        var manager = RunningManager(client, store);
        var assistant = new LocalAssistant(registry, store, manager);

        var outcome = await assistant.RunAsync("what cpu do I have", CancellationToken.None);

        Assert.False(outcome.IsRefused);
        Assert.Equal("Your CPU is a Test CPU.", outcome.Response!.Answer);
        var run = Assert.Single(outcome.Response.ToolsRun);
        Assert.Equal("get_system_overview", run.Name);
        Assert.True(run.Ok);
        Assert.Equal(1, tool.CallCount);
        Assert.Equal(2, client.ChatCallCount);

        // The tool result was fed back as a role:"tool" message on the second call.
        var secondCallMessages = client.SeenMessages[1];
        Assert.Contains(secondCallMessages, m => m.Role == "tool" && m.ToolName == "get_system_overview");
    }

    [Fact]
    public async Task RunAsync_consent_refusal_is_fed_back_to_the_model_as_the_tool_result()
    {
        var store = NewEnabledStore();
        store.Update(s => s.AiIntegration.AllowCooling = false);
        var tool = new FakeTool { Name = "apply_cooling_preset", Capability = McpCapability.Cooling, ReadOnly = false };
        var registry = new McpToolRegistry(new IMcpTool[] { tool }, store, new NullAuditSink());

        var client = new ScriptedOllamaClient(
            ToolCallResponse("apply_cooling_preset", "{\"preset\":\"silent\"}"),
            FinalAnswerResponse("I could not change the cooling preset."));
        var manager = RunningManager(client, store);
        var assistant = new LocalAssistant(registry, store, manager);

        var outcome = await assistant.RunAsync("make it quiet", CancellationToken.None);

        Assert.False(outcome.IsRefused);
        var run = Assert.Single(outcome.Response!.ToolsRun);
        Assert.False(run.Ok);
        Assert.Equal(0, tool.CallCount); // consent gate short-circuits before the tool body runs
        var toolMessage = client.SeenMessages[1][^1];
        Assert.Equal("tool", toolMessage.Role);
        Assert.Contains("Allow cooling control", toolMessage.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_stops_at_the_round_cap_when_the_model_never_gives_a_final_answer()
    {
        var store = NewEnabledStore();
        var tool = new FakeTool
        {
            Name = "get_sensors",
            Capability = McpCapability.Telemetry,
            ReadOnly = true,
            Behavior = _ => McpToolExecutionResult.Ok("{}"),
        };
        var registry = new McpToolRegistry(new IMcpTool[] { tool }, store, new NullAuditSink());

        var client = new ScriptedOllamaClient(alwaysToolCall: "get_sensors");
        var manager = RunningManager(client, store);
        var assistant = new LocalAssistant(registry, store, manager);

        var outcome = await assistant.RunAsync("keep checking sensors forever", CancellationToken.None);

        Assert.False(outcome.IsRefused);
        Assert.Equal(LocalAssistant.MaxRounds, client.ChatCallCount);
        Assert.Equal(LocalAssistant.MaxRounds, outcome.Response!.ToolsRun.Count);
        Assert.Equal(LocalAssistant.MaxRounds, tool.CallCount);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static OllamaChatResponse ToolCallResponse(string toolName, string argsJson) => new()
    {
        Message = new OllamaChatMessage
        {
            Role = "assistant",
            ToolCalls = new List<OllamaToolCall>
            {
                new()
                {
                    Function = new OllamaFunctionCall
                    {
                        Name = toolName,
                        Arguments = JsonDocument.Parse(argsJson).RootElement.Clone(),
                    },
                },
            },
        },
        Done = true,
    };

    private static OllamaChatResponse FinalAnswerResponse(string content) => new()
    {
        Message = new OllamaChatMessage { Role = "assistant", Content = content },
        Done = true,
    };

    private sealed class ScriptedOllamaClient : IOllamaClient
    {
        private readonly Queue<OllamaChatResponse> _responses;
        private readonly string? _alwaysToolCall;

        public ScriptedOllamaClient(params OllamaChatResponse[] responses)
        {
            _responses = new Queue<OllamaChatResponse>(responses);
        }

        public ScriptedOllamaClient(string alwaysToolCall)
        {
            _responses = new Queue<OllamaChatResponse>();
            _alwaysToolCall = alwaysToolCall;
        }

        public int ChatCallCount { get; private set; }
        public List<IReadOnlyList<OllamaChatMessage>> SeenMessages { get; } = new();

        public int Port => OllamaRuntimeManager.DefaultPort;

        public Task<string?> TryGetVersionAsync(CancellationToken ct) => Task.FromResult<string?>("0.32.1");

        public Task<IReadOnlyList<OllamaModelInfo>> ListModelsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<OllamaModelInfo>>(Array.Empty<OllamaModelInfo>());

        public Task<IReadOnlyList<OllamaModelInfo>> ListLoadedModelsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<OllamaModelInfo>>(Array.Empty<OllamaModelInfo>());

        public Task PullModelAsync(string model, Action<OllamaPullStatus> onProgress, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> DeleteModelAsync(string model, CancellationToken ct) => Task.FromResult(true);

        public Task<OllamaChatResponse> ChatAsync(
            string model, IReadOnlyList<OllamaChatMessage> messages, IReadOnlyList<OllamaToolDef>? tools, CancellationToken ct)
        {
            ChatCallCount++;
            SeenMessages.Add(new List<OllamaChatMessage>(messages));
            if (_alwaysToolCall is not null)
            {
                return Task.FromResult(ToolCallResponse(_alwaysToolCall, "{}"));
            }
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("ScriptedOllamaClient ran out of scripted responses.");
            }
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class FakeTool : IMcpTool
    {
        public string Name { get; init; } = "fake_tool";
        public string Title => "Fake Tool";
        public string Description => "A fake tool for LocalAssistant tests.";
        public McpCapability Capability { get; init; } = McpCapability.Telemetry;
        public bool ReadOnly { get; init; } = true;
        public string InputSchemaJson => "{\"type\":\"object\",\"properties\":{}}";
        public Func<JsonElement?, McpToolExecutionResult>? Behavior { get; init; }
        public int CallCount { get; private set; }

        public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(Behavior?.Invoke(args) ?? McpToolExecutionResult.Ok("{}"));
        }
    }

    private sealed class NullAuditSink : IMcpAuditSink
    {
        public void Record(McpAuditEntry entry)
        {
        }
    }

    private sealed class RecordingAuditSink : IMcpAuditSink
    {
        public List<McpAuditEntry> Entries { get; } = new();
        public void Record(McpAuditEntry entry) => Entries.Add(entry);
    }

    private sealed class NeverCalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP should not be used once a fake IOllamaClient is adopted.");
    }

    private sealed class NeverCalledProcessHost : IOllamaProcessHost
    {
        public IOllamaProcessHandle Start(string exePath, string workingDirectory, IReadOnlyDictionary<string, string> environment) =>
            throw new InvalidOperationException("The process host should not run when the fake client is detected as a system Ollama.");
    }
}
