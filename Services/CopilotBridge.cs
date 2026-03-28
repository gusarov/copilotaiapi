using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using CopilotAiApi.Models;

namespace CopilotAiApi.Services;

/// <summary>
/// Bridges OpenAI Chat Completion API requests to GitHub Copilot SDK sessions.
/// Manages a singleton CopilotClient and creates ephemeral sessions per request.
/// </summary>
public sealed class CopilotBridge : IAsyncDisposable
{
    private readonly ILogger<CopilotBridge> _logger;
    private readonly IConfiguration _configuration;
    private CopilotClient? _client;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public CopilotBridge(ILogger<CopilotBridge> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    private async Task<CopilotClient> GetClientAsync()
    {
        if (_initialized && _client != null) return _client;

        await _initLock.WaitAsync();
        try
        {
            if (_initialized && _client != null) return _client;

            var options = new CopilotClientOptions
            {
                AutoStart = false,
            };

            var cliPath = _configuration["Copilot:CliPath"];
            if (!string.IsNullOrEmpty(cliPath))
                options.CliPath = cliPath;

            var token = _configuration["Copilot:GitHubToken"];
            if (!string.IsNullOrEmpty(token))
                options.GitHubToken = token;

            _client = new CopilotClient(options);
            await _client.StartAsync();
            _initialized = true;
            _logger.LogInformation("Copilot SDK client started successfully");
            return _client;
        }
        finally
        {
            _initLock.Release();
        }
    }

    // ─── Non-Streaming Chat Completion ─────────────────────────────────

    public async Task<ChatCompletionResponse> ChatCompletionAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync();
        var completionId = $"chatcmpl-{Guid.NewGuid():N}";
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var (systemMessage, prompt) = BuildPromptFromMessages(request.Messages);
        var sessionConfig = BuildSessionConfig(request, systemMessage);

        await using var session = await client.CreateSessionAsync(sessionConfig);

        var responseContent = new StringBuilder();
        var toolRequests = new List<AssistantMessageDataToolRequestsItem>();
        int inputTokens = 0, outputTokens = 0;
        var sessionDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? errorMessage = null;

        session.On(evt =>
        {
            switch (evt)
            {
                case AssistantMessageEvent msg:
                    if (!string.IsNullOrEmpty(msg.Data.Content))
                        responseContent.Append(msg.Data.Content);
                    if (msg.Data.ToolRequests != null && msg.Data.ToolRequests.Any())
                        toolRequests.AddRange(msg.Data.ToolRequests);
                    break;

                case AssistantUsageEvent usage:
                    inputTokens += (int)(usage.Data.InputTokens ?? 0);
                    outputTokens += (int)(usage.Data.OutputTokens ?? 0);
                    break;

                case SessionIdleEvent:
                    sessionDone.TrySetResult();
                    break;

                case SessionErrorEvent err:
                    errorMessage = err.Data?.Message ?? "Unknown session error";
                    sessionDone.TrySetResult();
                    break;
            }
        });

        await session.SendAsync(new MessageOptions { Prompt = prompt });

        // Wait for session to complete (with timeout)
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

        try
        {
            await sessionDone.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Copilot session timed out after 5 minutes");
        }

        if (errorMessage != null)
        {
            _logger.LogWarning("Session error: {Error}", errorMessage);
            throw new InvalidOperationException($"Copilot session error: {errorMessage}");
        }

        // Check if model wants to call client-defined tools
        var clientToolNames = request.Tools?.Select(t => t.Function.Name).ToHashSet() ?? new HashSet<string>();
        var clientToolRequests = toolRequests
            .Where(tr => clientToolNames.Contains(tr.Name ?? ""))
            .ToList();

        if (clientToolRequests.Count > 0)
        {
            return BuildToolCallsResponse(completionId, created, request.Model,
                clientToolRequests, inputTokens, outputTokens);
        }

        return new ChatCompletionResponse
        {
            Id = completionId,
            Created = created,
            Model = request.Model,
            Choices = new List<Choice>
            {
                new()
                {
                    Index = 0,
                    Message = new ResponseMessage
                    {
                        Role = "assistant",
                        Content = responseContent.ToString(),
                    },
                    FinishReason = "stop",
                }
            },
            Usage = new Usage
            {
                PromptTokens = inputTokens,
                CompletionTokens = outputTokens,
                TotalTokens = inputTokens + outputTokens,
            }
        };
    }

    // ─── Streaming Chat Completion ─────────────────────────────────────

    public async Task StreamChatCompletionAsync(
        ChatCompletionRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync();
        var completionId = $"chatcmpl-{Guid.NewGuid():N}";
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var (systemMessage, prompt) = BuildPromptFromMessages(request.Messages);
        var sessionConfig = BuildSessionConfig(request, systemMessage);
        sessionConfig.Streaming = true;

        await using var session = await client.CreateSessionAsync(sessionConfig);

        httpContext.Response.ContentType = "text/event-stream";
        httpContext.Response.Headers["Cache-Control"] = "no-cache";
        httpContext.Response.Headers["Connection"] = "keep-alive";
        httpContext.Response.Headers["X-Accel-Buffering"] = "no";

        var clientToolNames = request.Tools?.Select(t => t.Function.Name).ToHashSet() ?? new HashSet<string>();
        var toolRequests = new List<AssistantMessageDataToolRequestsItem>();
        var sessionDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool roleSent = false;

        session.On(evt =>
        {
            switch (evt)
            {
                case AssistantMessageDeltaEvent delta:
                    if (!string.IsNullOrEmpty(delta.Data.DeltaContent))
                    {
                        var chunk = new ChatCompletionChunk
                        {
                            Id = completionId,
                            Created = created,
                            Model = request.Model,
                            Choices = new()
                            {
                                new StreamChoice
                                {
                                    Index = 0,
                                    Delta = new DeltaMessage
                                    {
                                        Role = roleSent ? null : "assistant",
                                        Content = delta.Data.DeltaContent,
                                    }
                                }
                            }
                        };
                        roleSent = true;
                        _ = WriteSseChunkAsync(httpContext, chunk, cancellationToken);
                    }
                    break;

                case AssistantMessageEvent msg:
                    if (msg.Data.ToolRequests != null && msg.Data.ToolRequests.Any())
                    {
                        var clientTools = msg.Data.ToolRequests
                            .Where(tr => clientToolNames.Contains(tr.Name ?? ""))
                            .ToList();
                        toolRequests.AddRange(clientTools);
                    }
                    break;

                case SessionIdleEvent:
                    sessionDone.TrySetResult();
                    break;

                case SessionErrorEvent:
                    sessionDone.TrySetResult();
                    break;
            }
        });

        // Send initial role chunk
        var roleChunk = new ChatCompletionChunk
        {
            Id = completionId,
            Created = created,
            Model = request.Model,
            Choices = new() { new StreamChoice { Delta = new DeltaMessage { Role = "assistant" } } }
        };
        await WriteSseChunkAsync(httpContext, roleChunk, cancellationToken);
        roleSent = true;

        await session.SendAsync(new MessageOptions { Prompt = prompt });

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

        try
        {
            await sessionDone.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) { /* best effort */ }

        // If tool calls were detected, stream them
        if (toolRequests.Count > 0)
        {
            var toolChunk = new ChatCompletionChunk
            {
                Id = completionId,
                Created = created,
                Model = request.Model,
                Choices = new()
                {
                    new StreamChoice
                    {
                        Index = 0,
                        Delta = new DeltaMessage
                        {
                            ToolCalls = toolRequests.Select((tr, idx) => new StreamToolCall
                            {
                                Index = idx,
                                Id = tr.ToolCallId ?? $"call_{Guid.NewGuid():N}",
                                Type = "function",
                                Function = new StreamFunctionCall
                                {
                                    Name = tr.Name,
                                    Arguments = tr.Arguments?.ToString() ?? "{}",
                                }
                            }).ToList()
                        },
                        FinishReason = null,
                    }
                }
            };
            await WriteSseChunkAsync(httpContext, toolChunk, cancellationToken);

            // Final chunk with finish_reason
            var doneChunk = new ChatCompletionChunk
            {
                Id = completionId, Created = created, Model = request.Model,
                Choices = new() { new StreamChoice { Delta = new DeltaMessage(), FinishReason = "tool_calls" } }
            };
            await WriteSseChunkAsync(httpContext, doneChunk, cancellationToken);
        }
        else
        {
            // Final chunk with stop reason
            var doneChunk = new ChatCompletionChunk
            {
                Id = completionId, Created = created, Model = request.Model,
                Choices = new() { new StreamChoice { Delta = new DeltaMessage(), FinishReason = "stop" } }
            };
            await WriteSseChunkAsync(httpContext, doneChunk, cancellationToken);
        }

        // Send [DONE]
        await httpContext.Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
        await httpContext.Response.Body.FlushAsync(cancellationToken);
    }

    // ─── Models Listing ────────────────────────────────────────────────

    public async Task<ModelsResponse> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync();
        try
        {
            var models = await client.ListModelsAsync();
            var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return new ModelsResponse
            {
                Data = models.Select(m => new ModelData
                {
                    Id = m.Id ?? "",
                    Created = created,
                    OwnedBy = "github-copilot",
                }).ToList()
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list models from Copilot SDK, returning defaults");
            return GetDefaultModels();
        }
    }

    // ─── Session Config Builder ────────────────────────────────────────

    private SessionConfig BuildSessionConfig(ChatCompletionRequest request, string? systemMessage)
    {
        var config = new SessionConfig
        {
            Model = request.Model,
            OnPermissionRequest = PermissionHandler.ApproveAll,
            Streaming = request.Stream,
            InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
        };

        if (!string.IsNullOrEmpty(request.ReasoningEffort))
            config.ReasoningEffort = request.ReasoningEffort;

        // System message
        if (!string.IsNullOrEmpty(systemMessage))
        {
            config.SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Replace,
                Content = systemMessage,
            };
        }

        // Register client-defined tools
        if (request.Tools?.Count > 0)
        {
            config.Tools = request.Tools
                .Select(t => (AIFunction)new ProxyAIFunction(
                    t.Function.Name,
                    t.Function.Description ?? "",
                    t.Function.Parameters))
                .ToList();

            // Disable built-in tools so only client tools are visible
            config.AvailableTools = new List<string>();
        }

        return config;
    }

    // ─── Prompt Builder ────────────────────────────────────────────────

    private static (string? systemMessage, string prompt) BuildPromptFromMessages(List<Models.ChatMessage> messages)
    {
        // Extract system messages
        var systemParts = messages
            .Where(m => m.Role == "system")
            .Select(m => m.GetContentAsString())
            .Where(s => !string.IsNullOrEmpty(s));
        var systemMessage = string.Join("\n\n", systemParts);
        if (string.IsNullOrWhiteSpace(systemMessage)) systemMessage = null;

        // Non-system messages
        var conversation = messages.Where(m => m.Role != "system").ToList();

        if (conversation.Count == 0)
            return (systemMessage, "Hello");

        // Single user message → use directly
        if (conversation.Count == 1 && conversation[0].Role == "user")
            return (systemMessage, conversation[0].GetContentAsString() ?? "Hello");

        // Multi-turn: reconstruct conversation as formatted prompt
        var sb = new StringBuilder();
        var lastMsg = conversation[^1];
        bool hasToolResults = conversation.Any(m => m.Role == "tool");

        // Format conversation history
        foreach (var msg in conversation)
        {
            switch (msg.Role)
            {
                case "user":
                    sb.AppendLine($"User: {msg.GetContentAsString()}");
                    break;

                case "assistant":
                    if (msg.ToolCalls?.Count > 0)
                    {
                        foreach (var tc in msg.ToolCalls)
                        {
                            sb.AppendLine($"Assistant called function `{tc.Function.Name}` with arguments: {tc.Function.Arguments}");
                        }
                    }
                    var content = msg.GetContentAsString();
                    if (!string.IsNullOrEmpty(content))
                    {
                        sb.AppendLine($"Assistant: {content}");
                    }
                    break;

                case "tool":
                    sb.AppendLine($"Function result (tool_call_id={msg.ToolCallId}): {msg.GetContentAsString()}");
                    break;
            }
        }

        // Add continuation instruction if last message is tool result
        if (lastMsg.Role == "tool")
        {
            sb.AppendLine();
            sb.AppendLine("Based on the function results above, provide your response to the user.");
        }

        return (systemMessage, sb.ToString().TrimEnd());
    }

    // ─── Response Builders ─────────────────────────────────────────────

    private static ChatCompletionResponse BuildToolCallsResponse(
        string completionId, long created, string model,
        List<AssistantMessageDataToolRequestsItem> toolRequests,
        int inputTokens, int outputTokens)
    {
        return new ChatCompletionResponse
        {
            Id = completionId,
            Created = created,
            Model = model,
            Choices = new()
            {
                new Choice
                {
                    Index = 0,
                    Message = new ResponseMessage
                    {
                        Role = "assistant",
                        Content = null,
                        ToolCalls = toolRequests.Select(tr => new ToolCallMessage
                        {
                            Id = tr.ToolCallId ?? $"call_{Guid.NewGuid():N}",
                            Type = "function",
                            Function = new FunctionCallMessage
                            {
                                Name = tr.Name ?? "",
                                Arguments = tr.Arguments?.ToString() ?? "{}",
                            }
                        }).ToList(),
                    },
                    FinishReason = "tool_calls",
                }
            },
            Usage = new Usage
            {
                PromptTokens = inputTokens,
                CompletionTokens = outputTokens,
                TotalTokens = inputTokens + outputTokens,
            }
        };
    }

    private static async Task WriteSseChunkAsync(
        HttpContext context, ChatCompletionChunk chunk, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(chunk, JsonOptions.Default);
        await context.Response.WriteAsync($"data: {json}\n\n", ct);
        await context.Response.Body.FlushAsync(ct);
    }

    private static ModelsResponse GetDefaultModels()
    {
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var defaultModels = new[] { "gpt-5", "gpt-4.1", "gpt-4o", "claude-sonnet-4.5", "claude-sonnet-4", "o3", "o4-mini" };
        return new ModelsResponse
        {
            Data = defaultModels.Select(m => new ModelData { Id = m, Created = created }).ToList()
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_client != null)
        {
            try { await _client.StopAsync(); }
            catch { /* best effort */ }
            await _client.DisposeAsync();
            _client = null;
        }
    }
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
