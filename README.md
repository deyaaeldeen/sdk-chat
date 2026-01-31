# SDK Chat

**Generate production-ready SDK samples in seconds using AI.**

![SDK Chat Demo](demo/demo.gif)

```bash
# One command. Production samples.
sdk-chat package sample generate ./your-sdk
```

## What It Does

Point it at any SDK. Get runnable samples with proper auth, error handling, and idiomatic patterns.

| Before | After |
|--------|-------|
| 10MB of source code | 5 clean, documented samples |
| Hours reading docs | 30 seconds |
| "How do I use this?" | Copy-paste examples |

---

## Quick Start

```bash
# Install
dotnet tool install --global sdk-chat

# Generate samples for any SDK
sdk-chat package sample generate /path/to/sdk
```

That's it. Auto-detects language, extracts API surface, generates samples.

---

## Supported Languages

| Language | Extractor | Requirement |
|----------|-----------|-------------|
| .NET/C# | Roslyn | Built-in |
| Python | `ast` | `python3` |
| TypeScript | ts-morph | `node` |
| Java | JavaParser | `jbang` |
| Go | go/parser | `go` |

---

## Usage

### CLI Mode

```bash
# Auto-detect language, generate 5 samples
sdk-chat package sample generate ./openai-dotnet

# Custom count + prompt
sdk-chat package sample generate ./openai-python \
  --count 10 \
  --prompt "streaming examples"

# Preview without writing files
sdk-chat package sample generate ./sdk --dry-run

# Use OpenAI instead of GitHub Copilot
OPENAI_API_KEY=sk-... sdk-chat --use-openai package sample generate ./sdk
```

### Options

| Option | Default | Description |
|--------|---------|-------------|
| `--output <dir>` | `samples/` | Output directory |
| `--count <n>` | `5` | Number of samples |
| `--language <lang>` | Auto | Force language detection |
| `--prompt <text>` | — | Custom generation prompt |
| `--model <name>` | — | Override AI model |
| `--budget <chars>` | `512K` | Max context size |
| `--dry-run` | `false` | Preview only |

### VS Code / Claude Desktop (MCP Mode)

```bash
sdk-chat mcp
```

**VS Code** — Add to `settings.json`:
```json
{
  "mcp.servers": {
    "sdk-chat": { "command": "sdk-chat", "args": ["mcp"] }
  }
}
```

**Claude Desktop** — Add to `claude_desktop_config.json`:
```json
{
  "mcpServers": {
    "sdk-chat": { "command": "sdk-chat", "args": ["mcp"] }
  }
}
```

### Interactive Mode (ACP)

```bash
sdk-chat acp
```

Guided generation with real-time feedback, permission prompts, and plan visualization.

---

## How It Works

```
SDK Source → API Extractor → Minimal JSON → AI → Samples
   10MB          ↓              ~100KB       ↓      5 files
              Roslyn/ast/                 Claude/
              ts-morph/etc               GPT/Copilot
```

1. **Detects** language from project files (`.csproj`, `pyproject.toml`, etc.)
2. **Extracts** public API surface — ~95% smaller than full source
3. **Generates** samples using AI with focused context
4. **Writes** idiomatic, runnable code with proper patterns

---

## Configuration

### Environment Variables

| Variable | Description |
|----------|-------------|
| `OPENAI_API_KEY` | API key (required with `--use-openai`) |
| `OPENAI_ENDPOINT` | Custom endpoint (OpenAI-compatible, etc.) |
| `SDK_CLI_MODEL` | Override default model |
| `SDK_CLI_DEBUG` | `true` to log prompts/responses |
| `SDK_CLI_DEBUG_DIR` | Directory for debug files |

### Project Config (Optional)

Create `.sdk-chat.json` in your SDK root:
```json
{
  "defaultLanguage": "dotnet",
  "samplesDirectory": "./samples"
}
```

---

## Build from Source

```bash
git clone https://github.com/deyaaeldeen/sdk-chat
cd sdk-chat
dotnet build
dotnet run --project Microsoft.SdkChat -- package sample generate /path/to/sdk
```

Run tests:
```bash
dotnet test  # 480+ tests
```

---

## Project Structure

```
sdk-chat/
├── Microsoft.SdkChat/           # Main CLI (Microsoft.SdkChat, MCP + ACP modes)
├── AgentClientProtocol.Sdk/  # ACP protocol implementation
├── ApiExtractor.DotNet/      # C# extractor (Roslyn)
├── ApiExtractor.Python/      # Python extractor (ast)
├── ApiExtractor.TypeScript/  # TS extractor (ts-morph)
├── ApiExtractor.Java/        # Java extractor (JavaParser)
├── ApiExtractor.Go/          # Go extractor (go/parser)
└── ApiExtractor.Tests/       # 140+ extractor tests
```

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for:
- Architecture overview
- Adding new language extractors
- Coding standards
- Testing requirements

---

## License

MIT
