using System.Text.Json;
using Microsoft.Extensions.AI;

namespace CopilotAiApi.Services;

/// <summary>
/// A custom AIFunction that accepts a pre-built JSON schema for tool registration.
/// Used to register OpenAI-style tool definitions with the Copilot SDK without
/// needing strongly-typed delegates. The handler is a no-op since we intercept
/// tool calls via AssistantMessageEvent.ToolRequests before execution.
/// </summary>
public sealed class ProxyAIFunction : AIFunction
{
    private readonly string _name;
    private readonly string _description;
    private readonly JsonElement _jsonSchema;
    private readonly Dictionary<string, object?> _additionalProps = new();

    public ProxyAIFunction(string name, string description, JsonElement? parametersSchema, bool overridesBuiltIn = false)
    {
        _name = name;
        _description = description;
        _jsonSchema = BuildFunctionSchema(name, description, parametersSchema);

        // Allow this tool to override a Copilot built-in tool with the same name (e.g. "bash")
        if (overridesBuiltIn)
        {
            _additionalProps["is_override"] = true;
        }
    }

    public override IReadOnlyDictionary<string, object?> AdditionalProperties => _additionalProps;

    public override string Name => _name;
    public override string Description => _description;
    public override JsonElement JsonSchema => _jsonSchema;

    protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
    {
        return new ValueTask<object?>("Tool call intercepted by bridge");
    }

    private static JsonElement BuildFunctionSchema(string name, string description, JsonElement? parameters)
    {
        var schema = new Dictionary<string, object?>
        {
            ["title"] = name,
            ["description"] = description,
            ["type"] = "object",
        };

        if (parameters.HasValue && parameters.Value.ValueKind == JsonValueKind.Object)
        {
            if (parameters.Value.TryGetProperty("properties", out var props))
                schema["properties"] = props;
            if (parameters.Value.TryGetProperty("required", out var req))
                schema["required"] = req;
            if (parameters.Value.TryGetProperty("additionalProperties", out var addl))
                schema["additionalProperties"] = addl;
        }

        var json = JsonSerializer.Serialize(schema);
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
