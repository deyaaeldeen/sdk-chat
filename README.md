# SDK Chat

**Generate production-ready SDK samples in seconds using AI.**

![SDK Chat Demo](demo/demo.gif)

```bash
sdk-chat package sample generate ./your-sdk
```

## Installation

```bash
dotnet tool install --global Microsoft.SdkChat
```

## Quick Start

```bash
# Generate 5 samples (default)
sdk-chat package sample generate ./openai-dotnet

# Use OpenAI instead of GitHub Copilot
sdk-chat package sample generate ./sdk --use-openai --load-dotenv

# Custom count + prompt
sdk-chat package sample generate ./sdk --count 10 --prompt "streaming examples"

# Preview without writing
sdk-chat package sample generate ./sdk --dry-run
```

## Supported Languages

| Language | Detection | Requirement |
|----------|-----------|-------------|
| .NET/C# | `.csproj`, `.sln` | Built-in |
| Python | `pyproject.toml`, `setup.py` | `python3` |
| TypeScript | `package.json` + `.ts` | `node` |
| JavaScript | `package.json` | `node` |
| Java | `pom.xml`, `build.gradle` | `jbang` |
| Go | `go.mod` | `go` |

---

## Commands

### `package sample generate`

Generate code samples for an SDK.

```bash
sdk-chat package sample generate <path> [options]
```

| Option | Default | Description |
|--------|---------|-------------|
| `--output <dir>` | Auto | Output directory for samples |
| `--language <lang>` | Auto | Force language: `dotnet`, `python`, `java`, `typescript`, `javascript`, `go` |
| `--count <n>` | `5` | Number of samples to generate |
| `--prompt <text>` | — | Custom generation prompt |
| `--model <name>` | `claude-sonnet-4.5` | AI model override |
| `--budget <chars>` | `512K` | Max context size |
| `--dry-run` | `false` | Preview without writing files |
| `--use-openai` | `false` | Use OpenAI API instead of GitHub Copilot |
| `--load-dotenv` | `false` | Load `.env` from current directory |

### `mcp`

Start MCP server for AI agent integration.

```bash
sdk-chat mcp [options]
```

| Option | Default | Description |
|--------|---------|-------------|
| `--transport <type>` | `stdio` | Transport: `stdio` or `sse` |
| `--port <n>` | `8080` | Port for SSE transport |
| `--log-level <level>` | `info` | Logging verbosity |
| `--use-openai` | `false` | Use OpenAI API |
| `--load-dotenv` | `false` | Load `.env` file |

**VS Code** (`settings.json`):
```json
{
  "mcp.servers": {
    "sdk-chat": { "command": "sdk-chat", "args": ["mcp"] }
  }
}
```

**Claude Desktop** (`claude_desktop_config.json`):
```json
{
  "mcpServers": {
    "sdk-chat": { "command": "sdk-chat", "args": ["mcp"] }
  }
}
```

### `acp`

Start interactive agent for guided sample generation.

```bash
sdk-chat acp [options]
```

| Option | Default | Description |
|--------|---------|-------------|
| `--log-level <level>` | `info` | Logging verbosity |
| `--use-openai` | `false` | Use OpenAI API |
| `--load-dotenv` | `false` | Load `.env` file |

### `doctor`

Validate external dependencies.

```bash
sdk-chat doctor
```

---

## Environment Variables

| Variable | Description |
|----------|-------------|
| `OPENAI_API_KEY` | OpenAI API key (required with `--use-openai`) |
| `OPENAI_ENDPOINT` | Custom OpenAI-compatible endpoint |
| `GH_TOKEN` | GitHub token for Copilot authentication |
| `GITHUB_TOKEN` | Alternative GitHub token |
| `SDK_CLI_MODEL` | Override default AI model |
| `SDK_CLI_TIMEOUT` | Request timeout in seconds |
| `SDK_CLI_DEBUG` | Set `true` to log prompts/responses |
| `SDK_CLI_DEBUG_DIR` | Directory for debug output files |
| `SDK_CLI_USE_OPENAI` | Set `true` to use OpenAI by default |
| `COPILOT_CLI_PATH` | Custom path to Copilot CLI binary |
| `NO_COLOR` | Disable colored output |

**Telemetry (optional):**

| Variable | Description |
|----------|-------------|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | OpenTelemetry collector endpoint |
| `OTEL_TRACES_EXPORTER` | Trace exporter type |
| `SDK_CLI_TELEMETRY_CONSOLE` | Print telemetry to console |

---

## Configuration File

Create `.sdk-chat.json` in your SDK root:

```json
{
  "defaultLanguage": "dotnet",
  "samplesDirectory": "./samples"
}
```

---

## How It Works

```
SDK Source → API Extractor → Minimal Context → AI → Samples
   10MB           ↓              ~100KB        ↓     5 files
             Roslyn/ast/                   Claude/
             ts-morph/etc                 GPT/Copilot
```

1. **Detect** — Language from project files
2. **Extract** — Public API surface (~95% smaller than source)
3. **Generate** — AI creates samples with focused context
4. **Write** — Idiomatic, runnable code with proper patterns

---

## Build from Source

```bash
git clone https://github.com/deyaaeldeen/sdk-chat
cd sdk-chat
dotnet build
dotnet run --project src/Microsoft.SdkChat -- package sample generate /path/to/sdk
```

Run tests:
```bash
dotnet test  # 480+ tests
```

---

## Project Structure

```
sdk-chat/
├── src/
│   ├── Microsoft.SdkChat/              # Main CLI tool
│   ├── AgentClientProtocol.Sdk/        # ACP protocol implementation
│   ├── ApiExtractor.Contracts/         # Shared extractor interfaces
│   ├── ApiExtractor.DotNet/            # C# extractor (Roslyn)
│   ├── ApiExtractor.Python/            # Python extractor (ast)
│   ├── ApiExtractor.TypeScript/        # TypeScript extractor (ts-morph)
│   ├── ApiExtractor.Java/              # Java extractor (JavaParser)
│   └── ApiExtractor.Go/                # Go extractor (go/parser)
├── tests/
│   ├── Microsoft.SdkChat.Tests/        # CLI tests
│   ├── AgentClientProtocol.Sdk.Tests/  # Protocol tests
│   └── ApiExtractor.Tests/             # Extractor tests
├── demo/                               # Demo recording
└── docs/                               # Documentation
```

See individual project READMEs in `src/` for implementation details.

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

MIT
