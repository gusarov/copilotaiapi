using Betalgo.Ranul.OpenAI;
using Betalgo.Ranul.OpenAI.Managers;
using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Betalgo.Ranul.OpenAI.ObjectModels.SharedModels;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CopilotAiApi.Tests;

/// <summary>
/// Acceptance tests that exercise the bridge via the Betalgo OpenAI client library.
/// This validates full OpenAI API contract compatibility — if Betalgo can parse
/// our responses, any OpenAI-compatible client should work too.
///
/// Requires Copilot CLI to be installed and authenticated.
/// Configure a token via user-secrets:
///   cd src/CopilotAiApi
///   dotnet user-secrets set "Copilot:GitHubToken" "ghu_..."
/// </summary>
[TestFixture]
[Category("Acceptance")]
public class ChatCompletionAcceptanceTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private OpenAIService _openAi = null!;
    private HttpClient _rawClient = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _factory = new WebApplicationFactory<Program>();
        // WAF's HttpClient uses an in-memory handler routed to TestServer.
        // Passing it to OpenAIService makes Betalgo talk to our bridge in-process.
        _rawClient = _factory.CreateClient();
        _openAi = new OpenAIService(new OpenAIOptions
        {
            ApiKey = "test-key",
            BaseDomain = _rawClient.BaseAddress!.ToString(),
            ValidateApiOptions = false,
        }, _rawClient);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _rawClient?.Dispose();
        _openAi?.Dispose();
        _factory?.Dispose();
    }

    // ─── Basic Chat Completion ───────────────────────────────────────

    [Test]
    public async Task SimplePrompt_ReturnsAssistantMessage()
    {
        var result = await _openAi.ChatCompletion.CreateCompletion(new ChatCompletionCreateRequest
        {
            Messages = [ChatMessage.FromUser("Reply with exactly the word 'pong' and nothing else.")],
            Model = "gpt-4o",
        });

        Assert.That(result.Successful, Is.True, () => $"API error: {result.Error?.Message}");
        Assert.That(result.Choices, Is.Not.Empty);
        Assert.That(result.Choices[0].FinishReason, Is.EqualTo("stop"));
        Assert.That(result.Choices[0].Message.Content, Does.Contain("pong").IgnoreCase);
    }

    [Test]
    public async Task SystemMessage_IsRespected()
    {
        var result = await _openAi.ChatCompletion.CreateCompletion(new ChatCompletionCreateRequest
        {
            Messages =
            [
                ChatMessage.FromSystem("You are a pirate. Every response MUST contain the word 'Arrr'."),
                ChatMessage.FromUser("Hello there"),
            ],
            Model = "gpt-4o",
        });

        Assert.That(result.Successful, Is.True, () => $"API error: {result.Error?.Message}");
        Assert.That(result.Choices[0].Message.Content, Does.Contain("Arrr").IgnoreCase);
    }

    [Test]
    public async Task MultiTurn_UnderstandsContext()
    {
        var result = await _openAi.ChatCompletion.CreateCompletion(new ChatCompletionCreateRequest
        {
            Messages =
            [
                ChatMessage.FromUser("My name is Zephyr."),
                ChatMessage.FromAssistant("Nice to meet you, Zephyr!"),
                ChatMessage.FromUser("What is my name? Reply with just the name."),
            ],
            Model = "gpt-4o",
        });

        Assert.That(result.Successful, Is.True, () => $"API error: {result.Error?.Message}");
        Assert.That(result.Choices[0].Message.Content, Does.Contain("Zephyr"));
    }

    // ─── Response Shape (OpenAI Contract) ────────────────────────────

    [Test]
    public async Task ResponseShape_HasAllRequiredFields()
    {
        var result = await _openAi.ChatCompletion.CreateCompletion(new ChatCompletionCreateRequest
        {
            Messages = [ChatMessage.FromUser("Say hi.")],
            Model = "gpt-4o",
        });

        Assert.That(result.Successful, Is.True, () => $"API error: {result.Error?.Message}");

        // Top-level
        Assert.That(result.Id, Does.StartWith("chatcmpl-"));
        Assert.That(result.ObjectTypeName, Is.EqualTo("chat.completion"));
        Assert.That(result.CreatedAtUnix, Is.GreaterThan(0));
        Assert.That(result.Model, Is.Not.Null.And.Not.Empty);

        // Choice
        Assert.That(result.Choices, Has.Count.GreaterThan(0));
        var choice = result.Choices[0];
        Assert.That(choice.Message.Content, Is.Not.Null);
        Assert.That(choice.FinishReason, Is.EqualTo("stop"));

        // Usage
        Assert.That(result.Usage, Is.Not.Null);
        Assert.That(result.Usage.TotalTokens, Is.GreaterThan(0));
        Assert.That(result.Usage.TotalTokens,
            Is.EqualTo(result.Usage.PromptTokens + (result.Usage.CompletionTokens ?? 0)));
    }

    // ─── Streaming ───────────────────────────────────────────────────

    [Test]
    public async Task Streaming_ReturnsChunks()
    {
        // Betalgo's CreateCompletionAsStream uses synchronous HttpClient.Send()
        // which TestServer doesn't support. Use the raw HttpClient with SSE instead.
        var requestBody = System.Text.Json.JsonSerializer.Serialize(new
        {
            model = "gpt-4o",
            messages = new[] { new { role = "user", content = "Count from 1 to 3, one number per line." } },
            stream = true,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Accept", "text/event-stream");

        using var response = await _rawClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.That(response.IsSuccessStatusCode, Is.True, $"HTTP {response.StatusCode}");

        var chunks = new List<string>();
        using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new System.IO.StreamReader(stream);

        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.StartsWith("data: ") && line != "data: [DONE]")
                chunks.Add(line["data: ".Length..]);
        }

        Assert.That(chunks, Is.Not.Empty, "Should receive at least one SSE chunk");

        // Parse chunks and combine content
        var fullContent = "";
        string? lastFinishReason = null;
        foreach (var json in chunks)
        {
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var choices = doc.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                if (choice.TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("content", out var content))
                    fullContent += content.GetString();
                if (choice.TryGetProperty("finish_reason", out var fr) &&
                    fr.ValueKind != System.Text.Json.JsonValueKind.Null)
                    lastFinishReason = fr.GetString();
            }
        }

        Assert.That(fullContent, Is.Not.Empty);
        Assert.That(fullContent, Does.Contain("1"));
        Assert.That(fullContent, Does.Contain("3"));
        Assert.That(lastFinishReason, Is.EqualTo("stop"));
    }

    // ─── Tool / Function Calling ─────────────────────────────────────

    private static List<ToolDefinition> WeatherTool =>
    [
        new ToolDefinition
        {
            Type = "function",
            Function = new FunctionDefinition
            {
                Name = "get_weather",
                Description = "Get the current weather in a given location",
                Parameters = new PropertyDefinition
                {
                    Type = "object",
                    Properties = new Dictionary<string, PropertyDefinition>
                    {
                        ["location"] = new()
                        {
                            Type = "string",
                            Description = "City name, e.g. San Francisco, CA",
                        },
                    },
                    Required = ["location"],
                },
            },
        },
    ];

    [Test]
    public async Task ToolCalling_ModelRequestsTool()
    {
        var result = await _openAi.ChatCompletion.CreateCompletion(new ChatCompletionCreateRequest
        {
            Messages = [ChatMessage.FromUser("What's the weather in London?")],
            Tools = WeatherTool,
            Model = "gpt-4o",
        });

        Assert.That(result.Successful, Is.True, () => $"API error: {result.Error?.Message}");
        Assert.That(result.Choices, Has.Count.EqualTo(1));
        Assert.That(result.Choices[0].FinishReason, Is.EqualTo("tool_calls"));

        var toolCalls = result.Choices[0].Message.ToolCalls;
        Assert.That(toolCalls, Is.Not.Null.And.Not.Empty);
        Assert.That(toolCalls![0].FunctionCall!.Name, Is.EqualTo("get_weather"));
        Assert.That(toolCalls[0].Id, Is.Not.Null.And.Not.Empty);

        // Arguments should be valid JSON containing "location"
        var args = System.Text.Json.JsonDocument.Parse(toolCalls[0].FunctionCall.Arguments!);
        Assert.That(args.RootElement.TryGetProperty("location", out var loc), Is.True);
        Assert.That(loc.GetString(), Does.Contain("London").IgnoreCase);
    }

    [Test]
    public async Task ToolCallRoundTrip_ModelUsesResult()
    {
        var toolCallId = "call_test_roundtrip";

        var result = await _openAi.ChatCompletion.CreateCompletion(new ChatCompletionCreateRequest
        {
            Messages =
            [
                ChatMessage.FromUser("What's the weather in Tokyo?"),
                new ChatMessage("assistant", content: "",
                    toolCalls:
                    [
                        new ToolCall
                        {
                            Id = toolCallId,
                            Type = "function",
                            FunctionCall = new FunctionCall
                            {
                                Name = "get_weather",
                                Arguments = "{\"location\":\"Tokyo\"}",
                            },
                        },
                    ]),
                ChatMessage.FromTool("{\"temp_c\": 22, \"condition\": \"Sunny\"}", toolCallId),
            ],
            Tools = WeatherTool,
            Model = "gpt-4o",
        });

        Assert.That(result.Successful, Is.True, () => $"API error: {result.Error?.Message}");
        Assert.That(result.Choices[0].FinishReason, Is.EqualTo("stop"));
        var content = result.Choices[0].Message.Content!;
        Assert.That(content, Does.Contain("22").Or.Contain("Sunny").Or.Contain("Tokyo"));
    }

    [Test]
    public async Task MultipleTools_ModelPicksCorrectOne()
    {
        var tools = new List<ToolDefinition>
        {
            new()
            {
                Type = "function",
                Function = new FunctionDefinition
                {
                    Name = "get_weather",
                    Description = "Get the current weather in a given location",
                    Parameters = new PropertyDefinition
                    {
                        Type = "object",
                        Properties = new Dictionary<string, PropertyDefinition>
                        {
                            ["location"] = new() { Type = "string" },
                        },
                        Required = ["location"],
                    },
                },
            },
            new()
            {
                Type = "function",
                Function = new FunctionDefinition
                {
                    Name = "get_time",
                    Description = "Get the current time in a given timezone or city",
                    Parameters = new PropertyDefinition
                    {
                        Type = "object",
                        Properties = new Dictionary<string, PropertyDefinition>
                        {
                            ["city"] = new() { Type = "string", Description = "City name" },
                        },
                        Required = ["city"],
                    },
                },
            },
        };

        var result = await _openAi.ChatCompletion.CreateCompletion(new ChatCompletionCreateRequest
        {
            Messages = [ChatMessage.FromUser("What time is it in Berlin?")],
            Tools = tools,
            Model = "gpt-4o",
        });

        Assert.That(result.Successful, Is.True, () => $"API error: {result.Error?.Message}");
        Assert.That(result.Choices[0].FinishReason, Is.EqualTo("tool_calls"));
        Assert.That(result.Choices[0].Message.ToolCalls![0].FunctionCall!.Name, Is.EqualTo("get_time"));
    }

    // ─── Error Handling ──────────────────────────────────────────────

    [Test]
    public async Task EmptyMessages_ReturnsError()
    {
        var result = await _openAi.ChatCompletion.CreateCompletion(new ChatCompletionCreateRequest
        {
            Messages = [],
            Model = "gpt-4o",
        });

        // Betalgo should parse the error response from our bridge
        Assert.That(result.Successful, Is.False);
    }
}
