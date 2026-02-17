# Configuration

## Environment Variables

| Variable | Description |
|----------|-------------|
| `OPENAI_API_KEY` | OpenAI API key (required with `--use-openai`) |
| `OPENAI_ENDPOINT` | Custom OpenAI-compatible endpoint |
| `GH_TOKEN` | GitHub token for Copilot authentication |
| `GITHUB_TOKEN` | Alternative GitHub token |
| `SDK_CLI_MODEL` | Override default AI model |
| `SDK_CLI_TIMEOUT` | AI request timeout in seconds (default: 300) |
| `SDK_CHAT_ENGINE_TIMEOUT` | Public API Graph Engine timeout in seconds (default: 300) |
| `SDK_CLI_DEBUG` | Set `true` to log prompts/responses |
| `SDK_CLI_DEBUG_DIR` | Directory for debug output files |
| `SDK_CLI_USE_OPENAI` | Set `true` to use OpenAI by default |
| `SDK_CLI_ACP_MAX_SESSIONS` | Maximum concurrent ACP sessions (default: 100) |
| `NO_COLOR` | Disable colored output |

## Telemetry (optional)

| Variable | Description |
|----------|-------------|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | OpenTelemetry collector endpoint |
| `OTEL_TRACES_EXPORTER` | Trace exporter type |
| `SDK_CLI_TELEMETRY_CONSOLE` | Print telemetry to console |

## Configuration File

Create `.sdk-chat.json` in your SDK root:

```json
{
  "defaultLanguage": "dotnet",
  "samplesDirectory": "./samples"
}
```

## Authentication Options

| Method | Environment Variable | Flag |
|--------|---------------------|------|
| GitHub Copilot | `GH_TOKEN` or `GITHUB_TOKEN` | (default) |
| Copilot credentials | Auto-mounted from `~/.copilot` | (default) |
| OpenAI | `OPENAI_API_KEY` | `--use-openai` |
