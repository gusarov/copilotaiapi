using System.Text.Json.Serialization;

namespace CopilotAiApi.Models;

public class ChatCompletionResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("object")]
    public string Object { get; set; } = "chat.completion";

    [JsonPropertyName("created")]
    public long Created { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("choices")]
    public List<Choice> Choices { get; set; } = new();

    [JsonPropertyName("usage")]
    public Usage? Usage { get; set; }

    [JsonPropertyName("system_fingerprint")]
    public string? SystemFingerprint { get; set; }
}

public class Choice
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("message")]
    public ResponseMessage Message { get; set; } = new();

    [JsonPropertyName("finish_reason")]
    public string FinishReason { get; set; } = "stop";

    [JsonPropertyName("logprobs")]
    public object? Logprobs { get; set; }
}

public class ResponseMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "assistant";

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ToolCallMessage>? ToolCalls { get; set; }

    [JsonPropertyName("refusal")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Refusal { get; set; }
}

public class Usage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }
}

public class ModelsResponse
{
    [JsonPropertyName("object")]
    public string Object { get; set; } = "list";

    [JsonPropertyName("data")]
    public List<ModelData> Data { get; set; } = new();
}

public class ModelData
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("object")]
    public string Object { get; set; } = "model";

    [JsonPropertyName("created")]
    public long Created { get; set; }

    [JsonPropertyName("owned_by")]
    public string OwnedBy { get; set; } = "github-copilot";
}

public class ErrorResponse
{
    [JsonPropertyName("error")]
    public ErrorDetail Error { get; set; } = new();
}

public class ErrorDetail
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "server_error";

    [JsonPropertyName("param")]
    public string? Param { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }
}
