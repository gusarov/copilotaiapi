using System.Text.Json;
using CopilotAiApi.Models;
using CopilotAiApi.Services;

var builder = WebApplication.CreateBuilder(args);

// Register the bridge as a singleton (manages CopilotClient lifecycle)
builder.Services.AddSingleton<CopilotBridge>();

// Configure JSON serialization for incoming requests
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
});

// CORS for browser-based clients
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

var app = builder.Build();
app.UseCors();

// ─── Health Check ──────────────────────────────────────────────────

app.MapGet("/", () => Results.Ok(new { status = "ok", service = "copilot-ai-api" }));

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// ─── OpenAI-Compatible Endpoints ───────────────────────────────────

app.MapPost("/v1/chat/completions", async (
    HttpContext context,
    CopilotBridge bridge,
    CancellationToken cancellationToken) =>
{
    ChatCompletionRequest? request;
    try
    {
        request = await JsonSerializer.DeserializeAsync<ChatCompletionRequest>(
            context.Request.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
            cancellationToken);
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(new ErrorResponse
        {
            Error = new ErrorDetail
            {
                Message = $"Invalid JSON: {ex.Message}",
                Type = "invalid_request_error",
            }
        });
    }

    if (request == null || request.Messages.Count == 0)
    {
        return Results.BadRequest(new ErrorResponse
        {
            Error = new ErrorDetail
            {
                Message = "messages is required and must be non-empty",
                Type = "invalid_request_error",
                Param = "messages",
            }
        });
    }

    try
    {
        if (request.Stream)
        {
            await bridge.StreamChatCompletionAsync(request, context, cancellationToken);
            return Results.Empty;
        }

        var response = await bridge.ChatCompletionAsync(request, cancellationToken);
        return Results.Json(response, JsonOptions.Default);
    }
    catch (TimeoutException ex)
    {
        return Results.Json(new ErrorResponse
        {
            Error = new ErrorDetail { Message = ex.Message, Type = "timeout_error" }
        }, statusCode: 504);
    }
    catch (Exception ex)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Chat completion error");
        return Results.Json(new ErrorResponse
        {
            Error = new ErrorDetail { Message = ex.Message, Type = "server_error" }
        }, statusCode: 500);
    }
});

// Also support without /v1 prefix (some clients use this)
app.MapPost("/chat/completions", async (HttpContext context, CopilotBridge bridge, CancellationToken ct) =>
{
    context.Request.Path = "/v1/chat/completions";
    // Redirect to the main handler
    var request = await JsonSerializer.DeserializeAsync<ChatCompletionRequest>(
        context.Request.Body,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);

    if (request == null || request.Messages.Count == 0)
        return Results.BadRequest(new ErrorResponse
        {
            Error = new ErrorDetail { Message = "messages is required", Type = "invalid_request_error" }
        });

    try
    {
        if (request.Stream)
        {
            await bridge.StreamChatCompletionAsync(request, context, ct);
            return Results.Empty;
        }
        return Results.Json(await bridge.ChatCompletionAsync(request, ct), JsonOptions.Default);
    }
    catch (Exception ex)
    {
        return Results.Json(new ErrorResponse
        {
            Error = new ErrorDetail { Message = ex.Message, Type = "server_error" }
        }, statusCode: 500);
    }
});

app.MapGet("/v1/models", async (CopilotBridge bridge, CancellationToken ct) =>
{
    try
    {
        var models = await bridge.ListModelsAsync(ct);
        return Results.Json(models, JsonOptions.Default);
    }
    catch (Exception ex)
    {
        return Results.Json(new ErrorResponse
        {
            Error = new ErrorDetail { Message = ex.Message, Type = "server_error" }
        }, statusCode: 500);
    }
});

app.MapGet("/models", async (CopilotBridge bridge, CancellationToken ct) =>
{
    try
    {
        var models = await bridge.ListModelsAsync(ct);
        return Results.Json(models, JsonOptions.Default);
    }
    catch (Exception ex)
    {
        return Results.Json(new ErrorResponse
        {
            Error = new ErrorDetail { Message = ex.Message, Type = "server_error" }
        }, statusCode: 500);
    }
});

// Graceful shutdown
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    var bridge = app.Services.GetRequiredService<CopilotBridge>();
    bridge.DisposeAsync().AsTask().GetAwaiter().GetResult();
});

app.Run();

// Expose Program for WebApplicationFactory in tests
public partial class Program { }
