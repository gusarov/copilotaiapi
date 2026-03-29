using System.Text.Json;

namespace CopilotAiApi.Middleware;

/// <summary>
/// Validates requests using Bearer tokens configured in the "ApiKeys" section.
/// Config example:
///   "ApiKeys": {
///     "code_review_bot": "sk-abc123",
///     "my_service":      "sk-def456"
///   }
///
/// Clients pass: Authorization: Bearer sk-abc123
///
/// Exempt paths: /, /health
/// If no ApiKeys are configured, auth is disabled (convenient for local use).
/// </summary>
public class ApiKeyAuthMiddleware(RequestDelegate next, IConfiguration configuration)
{
    private static readonly HashSet<string> PublicPaths = ["/", "/health"];

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        if (PublicPaths.Contains(path))
        {
            await next(context);
            return;
        }

        // Load valid tokens from config (values of all entries under ApiKeys)
        var apiKeysSection = configuration.GetSection("ApiKeys");
        var configuredKeys = apiKeysSection.GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToHashSet();

        // If no keys are configured, auth is disabled (open/local mode)
        if (configuredKeys.Count == 0)
        {
            await next(context);
            return;
        }

        // Extract Bearer token from Authorization header
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        string? token = null;
        if (authHeader != null && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = authHeader["Bearer ".Length..].Trim();

        if (token == null || !configuredKeys.Contains(token))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            var body = new
            {
                error = new
                {
                    message = token == null
                        ? "No API key provided. Pass your API key in the Authorization header: Authorization: Bearer YOUR_API_KEY"
                        : "Invalid API key provided.",
                    type = "invalid_request_error",
                    code = "invalid_api_key",
                }
            };
            await context.Response.WriteAsync(JsonSerializer.Serialize(body));
            return;
        }

        await next(context);
    }
}
