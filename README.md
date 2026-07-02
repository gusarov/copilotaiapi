# Copilot AI API

An OpenAI-compatible HTTP server that bridges the [GitHub Copilot SDK](https://github.com/github/copilot-sdk) to the standard Chat Completion API format.

Any client that speaks the OpenAI `/v1/chat/completions` protocol (e.g., `openai-python`, `curl`, LangChain, etc.) can now use GitHub Copilot models — including **function/tool calling** and **streaming**.

## Quick Start

### Prerequisites

- .NET 9.0+ SDK
- [GitHub Copilot CLI](https://docs.github.com/en/copilot/how-tos/set-up/install-copilot-cli) installed and authenticated

### Run

```bash
dotnet run
```

The server starts on `http://localhost:7777` by default.

### Test

```bash
# Simple chat
curl http://localhost:7777/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "claude-opus-4.8",
    "messages": [{"role": "user", "content": "Hello!"}]
  }'

# List available models
curl http://localhost:7777/v1/models
```

## Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/v1/chat/completions` | Chat Completion (OpenAI-compatible) |
| `POST` | `/chat/completions` | Alias without `/v1` prefix |
| `GET` | `/v1/models` | List available models |
| `GET` | `/models` | Alias without `/v1` prefix |
| `GET` | `/health` | Health check |

## Features

### Chat Completion
Standard multi-turn conversations with system/user/assistant messages.

```json
{
  "model": "claude-opus-4.8",
  "messages": [
    {"role": "system", "content": "You are a helpful assistant."},
    {"role": "user", "content": "Explain quantum computing in simple terms."}
  ]
}
```

### Streaming (SSE)
Set `"stream": true` to receive Server-Sent Events:

```bash
curl http://localhost:7777/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model": "claude-opus-4.8", "messages": [{"role": "user", "content": "Tell me a story"}], "stream": true}'
```

### Function / Tool Calling
Define tools in the request. When the model wants to call a function, it returns `tool_calls` in the response — exactly like the OpenAI API:

```json
{
  "model": "claude-opus-4.8",
  "messages": [{"role": "user", "content": "What is the weather in NYC?"}],
  "tools": [{
    "type": "function",
    "function": {
      "name": "get_weather",
      "description": "Get the current weather for a city",
      "parameters": {
        "type": "object",
        "properties": {
          "city": {"type": "string", "description": "City name"}
        },
        "required": ["city"]
      }
    }
  }]
}
```

The response will contain:
```json
{
  "choices": [{
    "message": {
      "role": "assistant",
      "tool_calls": [{
        "id": "call_abc123",
        "type": "function",
        "function": {
          "name": "get_weather",
          "arguments": "{\"city\": \"NYC\"}"
        }
      }]
    },
    "finish_reason": "tool_calls"
  }]
}
```

Send the result back in the next request:
```json
{
  "model": "claude-opus-4.8",
  "messages": [
    {"role": "user", "content": "What is the weather in NYC?"},
    {"role": "assistant", "tool_calls": [{"id": "call_abc123", "type": "function", "function": {"name": "get_weather", "arguments": "{\"city\":\"NYC\"}"}}]},
    {"role": "tool", "tool_call_id": "call_abc123", "content": "{\"temp\": 72, \"condition\": \"sunny\"}"}
  ],
  "tools": [...]
}
```

### Model Selection
Use any model available through your Copilot subscription. If you omit `model`, the
bridge defaults to `claude-opus-4.8`. Call `GET /v1/models` to see the live list for
your account.

```json
{"model": "claude-opus-4.8"}
{"model": "claude-sonnet-4.6"}
{"model": "gpt-5.5"}
{"model": "auto"}
```

> Note: the Copilot CLI silently falls back to a default model for unknown ids, so
> prefer ids returned by `/v1/models`.

### Reasoning Effort
For models that support it:

```json
{"model": "claude-opus-4.8", "reasoning_effort": "high"}
```

## Configuration

Edit `appsettings.json` or use environment variables:

| Setting | Env Var | Description |
|---------|---------|-------------|
| `Copilot:GitHubToken` | `COPILOT_GITHUB_TOKEN` | GitHub token for authentication (optional; uses the logged-in Copilot CLI by default) |
| `Copilot:FallbackModels` | `COPILOT__FALLBACKMODELS__0`, `…__1`, … | Model ids returned by `/v1/models` only if the live SDK lookup has never succeeded |
| `Urls` | `ASPNETCORE_URLS` | Server listen URL (default: `http://0.0.0.0:7777`) |

> The Copilot CLI must be installed and on `PATH`. `Copilot:CliPath` is no longer
> used (the SDK 1.0+ resolves the CLI automatically).

## Architecture

```
OpenAI Client (curl, Python, etc.)
       ↓  HTTP (OpenAI format)
  Copilot AI API Server (this project)
       ↓  JSON-RPC
  Copilot CLI (server mode)
       ↓
  GitHub Copilot Models
```

Each HTTP request creates an ephemeral Copilot SDK session. The full message history from the request is reconstructed as context for the session.

## Using with Python (OpenAI client)

```python
from openai import OpenAI

client = OpenAI(
    base_url="http://localhost:7777/v1",
    api_key="not-needed"  # Auth handled by Copilot CLI
)

response = client.chat.completions.create(
    model="claude-opus-4.8",
    messages=[{"role": "user", "content": "Hello!"}]
)
print(response.choices[0].message.content)
```

## License

MIT
