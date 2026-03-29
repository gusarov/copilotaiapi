using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopilotAiApi.Models;

public class ChatCompletionRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "gpt-4o";

    [JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; set; } = new();

    [JsonPropertyName("tools")]
    public List<ToolDefinition>? Tools { get; set; }

    [JsonPropertyName("tool_choice")]
    public JsonElement? ToolChoice { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("max_completion_tokens")]
    public int? MaxCompletionTokens { get; set; }

    [JsonPropertyName("top_p")]
    public double? TopP { get; set; }

    [JsonPropertyName("frequency_penalty")]
    public double? FrequencyPenalty { get; set; }

    [JsonPropertyName("presence_penalty")]
    public double? PresencePenalty { get; set; }

    [JsonPropertyName("stop")]
    public JsonElement? Stop { get; set; }

    [JsonPropertyName("user")]
    public string? User { get; set; }

    [JsonPropertyName("n")]
    public int? N { get; set; }

    [JsonPropertyName("reasoning_effort")]
    public string? ReasoningEffort { get; set; }

    [JsonPropertyName("response_format")]
    public ResponseFormat? ResponseFormat { get; set; }
}

public class ResponseFormat
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    /// <summary>Used when Type is "json_schema".</summary>
    [JsonPropertyName("json_schema")]
    public JsonSchemaFormat? JsonSchema { get; set; }
}

public class JsonSchemaFormat
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("schema")]
    public JsonElement? Schema { get; set; }

    [JsonPropertyName("strict")]
    public bool? Strict { get; set; }
}

public class ChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("content")]
    public JsonElement? Content { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<ToolCallMessage>? ToolCalls { get; set; }

    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }

    [JsonPropertyName("refusal")]
    public string? Refusal { get; set; }

    public string? GetContentAsString()
    {
        if (Content == null || Content.Value.ValueKind == JsonValueKind.Null)
            return null;
        if (Content.Value.ValueKind == JsonValueKind.String)
            return Content.Value.GetString();
        if (Content.Value.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var part in Content.Value.EnumerateArray())
            {
                if (part.TryGetProperty("type", out var type) &&
                    type.GetString() == "text" &&
                    part.TryGetProperty("text", out var text))
                {
                    parts.Add(text.GetString() ?? "");
                }
            }
            return string.Join("\n", parts);
        }
        return Content.Value.ToString();
    }
}

public class ToolCallMessage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public FunctionCallMessage Function { get; set; } = new();
}

public class FunctionCallMessage
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = "";
}

public class ToolDefinition
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public FunctionDefinition Function { get; set; } = new();
}

public class FunctionDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("parameters")]
    public JsonElement? Parameters { get; set; }

    [JsonPropertyName("strict")]
    public bool? Strict { get; set; }
}
