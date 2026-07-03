using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using GitHub.Copilot;
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

    // Last successfully retrieved live model list, reused as the fallback when
    // a later ListModelsAsync call fails (CLI unavailable, transient error, etc.).
    private volatile ModelsResponse? _cachedModels;

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
                // SDK 1.0+ discovers and launches the `copilot` CLI automatically.
                Mode = CopilotClientMode.CopilotCli,
            };

            // Note: Copilot:CliPath is no longer supported by the SDK (1.0+ resolves
            // the CLI from PATH). Surface a hint if it was configured.
            var cliPath = _configuration["Copilot:CliPath"];
            if (!string.IsNullOrEmpty(cliPath))
                _logger.LogWarning("Copilot:CliPath is set but no longer supported by the SDK; ensure the 'copilot' CLI is on PATH.");

            var token = _configuration["Copilot:GitHubToken"];
            if (!string.IsNullOrEmpty(token))
                options.GitHubToken = token;

            _client = new CopilotClient(options);
            await _client.StartAsync();
            _initialized = true;
            _logger.LogInformation("Copilot SDK client started successfully");

            // Warm up the freshly launched CLI. A cold CLI's first response can
            // otherwise surface as an SDK event-deserialization error on the very
            // first chat request; priming the model list negotiates the protocol
            // and primes the catalog cache before real traffic arrives.
            // Safe to call ListModelsAsync here: _initialized is already true, so
            // the re-entrant GetClientAsync returns before taking _initLock.
            try
            {
                var warm = await ListModelsAsync();
                _logger.LogInformation("Copilot SDK client warmed up ({Count} models)",
                    warm.Data.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Copilot SDK warmup (ListModelsAsync) failed; continuing without warmup");
            }

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
        var toolRequests = new List<AssistantMessageToolRequest>();
        int inputTokens = 0, outputTokens = 0;
        string? actualModel = null;
        var sessionDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? errorMessage = null;

        // The SDK may invoke this callback from arbitrary threads. A single lock
        // guards every shared mutable field so reads and writes are atomic and
        // properly published across threads — no Interlocked/volatile juggling.
        var gate = new object();

        session.On<SessionEvent>(evt =>
        {
            _logger.LogDebug("Event received: {EventType}", evt.GetType().Name);
            lock (gate)
            {
                switch (evt)
                {
                    case AssistantMessageEvent msg:
                        if (!string.IsNullOrEmpty(msg.Data.Model))
                            actualModel = msg.Data.Model;
                        if (!string.IsNullOrEmpty(msg.Data.Content))
                            responseContent.Append(msg.Data.Content);
                        if (msg.Data.ToolRequests != null && msg.Data.ToolRequests.Any())
                        {
                            _logger.LogInformation("Tool requests received: {Count}", msg.Data.ToolRequests.Count());
                            foreach (var tr in msg.Data.ToolRequests)
                                _logger.LogInformation("  Tool: {Name}, Args: {Args}", tr.Name, tr.Arguments);
                            toolRequests.AddRange(msg.Data.ToolRequests);
                        }
                        break;

                    case ToolExecutionStartEvent toolStart:
                        _logger.LogInformation("ToolExecutionStart: {ToolName} (CallId: {CallId})",
                            toolStart.Data.ToolName, toolStart.Data.ToolCallId);
                        break;

                    case AssistantUsageEvent usage:
                        if (!string.IsNullOrEmpty(usage.Data.Model))
                            actualModel = usage.Data.Model;
                        inputTokens += (int)(usage.Data.InputTokens ?? 0);
                        outputTokens += (int)(usage.Data.OutputTokens ?? 0);
                        break;

                    case SessionIdleEvent:
                        sessionDone.TrySetResult();
                        break;

                    case SessionErrorEvent err:
                        errorMessage = err.Data?.Message ?? "Unknown session error";
                        _logger.LogWarning("SessionError: {Error}", errorMessage);
                        sessionDone.TrySetResult();
                        break;

                    default:
                        _logger.LogDebug("Unhandled event: {EventType} - {Data}", evt.GetType().Name,
                            JsonSerializer.Serialize(evt, new JsonSerializerOptions { WriteIndented = false }));
                        break;
                }
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

        // Snapshot all shared state under the lock for a consistent, safely
        // published view (the SDK handler may run on other threads).
        string? errMsg;
        List<AssistantMessageToolRequest> capturedToolRequests;
        string? capturedModel;
        int capturedInput, capturedOutput;
        string capturedContent;
        lock (gate)
        {
            errMsg = errorMessage;
            capturedToolRequests = toolRequests.ToList();
            capturedModel = actualModel;
            capturedInput = inputTokens;
            capturedOutput = outputTokens;
            capturedContent = responseContent.ToString();
        }

        if (errMsg != null)
        {
            _logger.LogWarning("Session error: {Error}", errMsg);
            throw new InvalidOperationException($"Copilot session error: {errMsg}");
        }

        // Check if model wants to call client-defined tools
        var clientToolNames = request.Tools?.Select(t => t.Function.Name).ToHashSet() ?? new HashSet<string>();
        var clientToolRequests = capturedToolRequests
            .Where(tr => clientToolNames.Contains(tr.Name ?? ""))
            .ToList();

        // Echo the model that actually served the request (from the SDK event),
        // falling back to the requested id if the SDK did not report one.
        var responseModel = capturedModel ?? request.Model;

        if (clientToolRequests.Count > 0)
        {
            return BuildToolCallsResponse(completionId, created, responseModel,
                clientToolRequests, capturedInput, capturedOutput);
        }

        return new ChatCompletionResponse
        {
            Id = completionId,
            Created = created,
            Model = responseModel,
            Choices = new List<Choice>
            {
                new()
                {
                    Index = 0,
                    Message = new ResponseMessage
                    {
                        Role = "assistant",
                        Content = SanitizeContent(capturedContent, request.ResponseFormat),
                    },
                    FinishReason = "stop",
                }
            },
            Usage = new Usage
            {
                PromptTokens = capturedInput,
                CompletionTokens = capturedOutput,
                TotalTokens = capturedInput + capturedOutput,
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
        var toolRequests = new List<AssistantMessageToolRequest>();
        string? actualModel = null;
        var sessionDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // All SSE chunks flow through this channel and are written by the single
        // consumer loop below in arrival order. HttpResponse.Body does NOT support
        // concurrent/overlapping writes, so the event handler must never write
        // directly — it only enqueues.
        var chunks = Channel.CreateUnbounded<ChatCompletionChunk>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        // Guards the shared mutable state below (roleSent, actualModel, toolRequests).
        // The SDK may raise events from arbitrary threads; the consumer (FinalizeAsync)
        // also reads this state, so every access goes through this single lock.
        var gate = new object();
        bool roleSent = false;

        session.On<SessionEvent>(evt =>
        {
            lock (gate)
            {
                switch (evt)
                {
                    case AssistantMessageDeltaEvent delta:
                        if (!string.IsNullOrEmpty(delta.Data.DeltaContent))
                        {
                            var first = !roleSent;
                            roleSent = true;
                            chunks.Writer.TryWrite(new ChatCompletionChunk
                            {
                                Id = completionId,
                                Created = created,
                                Model = actualModel ?? request.Model,
                                Choices = new()
                                {
                                    new StreamChoice
                                    {
                                        Index = 0,
                                        Delta = new DeltaMessage
                                        {
                                            Role = first ? "assistant" : null,
                                            Content = delta.Data.DeltaContent,
                                        }
                                    }
                                }
                            });
                        }
                        break;

                    case AssistantMessageEvent msg:
                        if (!string.IsNullOrEmpty(msg.Data.Model))
                            actualModel = msg.Data.Model;
                        if (msg.Data.ToolRequests != null && msg.Data.ToolRequests.Any())
                        {
                            var clientTools = msg.Data.ToolRequests
                                .Where(tr => clientToolNames.Contains(tr.Name ?? ""))
                                .ToList();
                            toolRequests.AddRange(clientTools);
                        }
                        break;

                    case AssistantUsageEvent usage:
                        if (!string.IsNullOrEmpty(usage.Data.Model))
                            actualModel = usage.Data.Model;
                        break;

                    case SessionIdleEvent:
                        sessionDone.TrySetResult();
                        break;

                    case SessionErrorEvent:
                        sessionDone.TrySetResult();
                        break;
                }
            }
        });

        // Initial role chunk, enqueued before SendAsync so it is the first thing written.
        string? initialModel;
        lock (gate)
        {
            roleSent = true;
            initialModel = actualModel;
        }
        chunks.Writer.TryWrite(new ChatCompletionChunk
        {
            Id = completionId,
            Created = created,
            Model = initialModel ?? request.Model,
            Choices = new() { new StreamChoice { Delta = new DeltaMessage { Role = "assistant" } } }
        });

        await session.SendAsync(new MessageOptions { Prompt = prompt });

        // Waits for the session to finish, enqueues the terminal chunks, then closes
        // the channel. Runs concurrently with the writer loop so deltas stream out as
        // they arrive instead of being buffered until completion.
        async Task FinalizeAsync()
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                await sessionDone.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) { /* best effort */ }

            // Snapshot shared state under the lock; the SDK event handler may have
            // mutated it on another thread right up until SessionIdle/Error.
            List<AssistantMessageToolRequest> finalToolRequests;
            string finalModel;
            lock (gate)
            {
                finalToolRequests = toolRequests.ToList();
                finalModel = actualModel ?? request.Model;
            }

            if (finalToolRequests.Count > 0)
            {
                chunks.Writer.TryWrite(new ChatCompletionChunk
                {
                    Id = completionId,
                    Created = created,
                    Model = finalModel,
                    Choices = new()
                    {
                        new StreamChoice
                        {
                            Index = 0,
                            Delta = new DeltaMessage
                            {
                                ToolCalls = finalToolRequests.Select((tr, idx) => new StreamToolCall
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
                });
                chunks.Writer.TryWrite(new ChatCompletionChunk
                {
                    Id = completionId, Created = created, Model = finalModel,
                    Choices = new() { new StreamChoice { Delta = new DeltaMessage(), FinishReason = "tool_calls" } }
                });
            }
            else
            {
                chunks.Writer.TryWrite(new ChatCompletionChunk
                {
                    Id = completionId, Created = created, Model = finalModel,
                    Choices = new() { new StreamChoice { Delta = new DeltaMessage(), FinishReason = "stop" } }
                });
            }

            chunks.Writer.TryComplete();
        }

        var finalizeTask = FinalizeAsync();

        try
        {
            // Single writer: drains the channel and writes chunks in order.
            await foreach (var chunk in chunks.Reader.ReadAllAsync(cancellationToken))
                await WriteSseChunkAsync(httpContext, chunk, cancellationToken);
        }
        finally
        {
            chunks.Writer.TryComplete();
            try { await finalizeTask; } catch { /* best effort */ }
        }

        if (cancellationToken.IsCancellationRequested)
            return;

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
            var response = new ModelsResponse
            {
                Data = models.Select(m => new ModelData
                {
                    Id = m.Id ?? "",
                    Created = created,
                    OwnedBy = "github-copilot",
                }).ToList()
            };

            // Cache the live list so a later failure can serve real data
            // instead of the static fallback.
            if (response.Data.Count > 0)
                _cachedModels = response;

            return response;
        }
        catch (Exception ex)
        {
            // Prefer the last successful live list; only resort to the static
            // configurable fallback if we have never retrieved one.
            if (_cachedModels != null)
            {
                _logger.LogWarning(ex, "Failed to list models from Copilot SDK, returning last cached list");
                return _cachedModels;
            }

            _logger.LogWarning(ex, "Failed to list models from Copilot SDK, returning configured fallback");
            return GetFallbackModels();
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

        // Append response_format instructions to system message so the model honours them
        var effectiveSystemMessage = BuildSystemMessageWithFormat(systemMessage, request.ResponseFormat);

        // Always replace system message to prevent Copilot's built-in prompt from interfering
        config.SystemMessage = new SystemMessageConfig
        {
            Mode = SystemMessageMode.Replace,
            Content = effectiveSystemMessage,
        };

        // Register client-defined tools
        if (request.Tools?.Count > 0)
        {
            config.Tools = request.Tools
                .Select(t => (AIFunctionDeclaration)new ProxyAIFunction(
                            t.Function.Name,
                            t.Function.Description ?? "",
                            t.Function.Parameters,
                            overridesBuiltIn: true))
                .ToList();
            // Enable only client-defined tools, disable all built-in tools
            config.AvailableTools = request.Tools.Select(t => t.Function.Name).ToList();
        }

        return config;
    }

    /// <summary>
    /// Builds the effective system message, appending JSON output instructions
    /// when response_format is "json_object" or "json_schema".
    /// </summary>
    private static string BuildSystemMessageWithFormat(string? systemMessage, ResponseFormat? format)
    {
        var baseMessage = systemMessage ?? "You are a helpful assistant.";

        if (format == null || format.Type == "text")
            return baseMessage;

        var sb = new StringBuilder(baseMessage);

        if (format.Type == "json_object")
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("IMPORTANT: You must respond with valid JSON only. Do not include any text outside of the JSON object. Do not use markdown code blocks.");
        }
        else if (format.Type == "json_schema" && format.JsonSchema != null)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("IMPORTANT: You must respond with valid JSON only that strictly conforms to the following JSON schema. Do not include any text outside of the JSON object. Do not use markdown code blocks.");
            if (!string.IsNullOrEmpty(format.JsonSchema.Description))
            {
                sb.AppendLine($"Schema description: {format.JsonSchema.Description}");
            }
            if (format.JsonSchema.Schema.HasValue)
            {
                sb.AppendLine("Schema:");
                sb.AppendLine(format.JsonSchema.Schema.Value.ToString());
            }
        }

        return sb.ToString().TrimEnd();
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
        List<AssistantMessageToolRequest> toolRequests,
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

    /// <summary>
    /// Strips markdown code fences that the model may wrap around JSON output,
    /// e.g. ```json\n{...}\n``` → {...}
    /// Only applied when response_format requests JSON.
    /// </summary>
    private static string SanitizeContent(string content, ResponseFormat? format)
    {
        if (format == null || (format.Type != "json_object" && format.Type != "json_schema"))
            return content;

        var trimmed = content.Trim();

        // Strip opening fence: ```json or ```
        if (trimmed.StartsWith("```"))
        {
            var newlineIdx = trimmed.IndexOf('\n');
            if (newlineIdx >= 0)
                trimmed = trimmed[(newlineIdx + 1)..];
        }

        // Strip closing fence: ```
        if (trimmed.EndsWith("```"))
            trimmed = trimmed[..^3];

        return trimmed.Trim();
    }

    private ModelsResponse GetFallbackModels()
    {
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Allow overriding the static fallback via configuration
        // (appsettings.json "Copilot:FallbackModels" or COPILOT__FALLBACKMODELS env var).
        var configured = _configuration.GetSection("Copilot:FallbackModels").Get<string[]>();
        var fallbackModels = configured is { Length: > 0 }
            ? configured
            : DefaultFallbackModels;

        return new ModelsResponse
        {
            Data = fallbackModels
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Select(m => new ModelData { Id = m, Created = created })
                .ToList()
        };
    }

    private static readonly string[] DefaultFallbackModels =
    {
        "claude-opus-4.8", "claude-opus-4.7", "claude-sonnet-4.6", "claude-sonnet-4.5",
        "gpt-5.5", "gpt-5.4", "gpt-5-mini", "gemini-3.5-flash"
    };

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
