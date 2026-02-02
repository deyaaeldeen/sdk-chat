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
| `SDK_CLI_TIMEOUT` | AI request timeout in seconds (default: 300) |
| `SDK_CHAT_EXTRACTOR_TIMEOUT` | API extractor timeout in seconds (default: 300) |
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

---

## Docker

### Quick Start

```bash
# Generate samples (Linux/macOS) - auto-builds image if needed
export GH_TOKEN="ghp_..."
./scripts/sdk-chat.sh package sample generate /path/to/sdk

# Generate samples (Windows PowerShell)
$env:GH_TOKEN = "ghp_..."
.\scripts\sdk-chat.ps1 package sample generate C:\path\to\sdk
```

### Wrapper Scripts (Recommended)

The wrapper scripts handle volume mounts and environment variables automatically:

| Script | Platform | Location |
|--------|----------|----------|
| `sdk-chat.sh` | Linux/macOS | `scripts/sdk-chat.sh` |
| `sdk-chat.ps1` | Windows | `scripts/sdk-chat.ps1` |

**Authentication** (choose one):
- GitHub token: `export GH_TOKEN="ghp_..."` or `GITHUB_TOKEN`
- Copilot credentials: Scripts auto-mount `~/.copilot` if present
- OpenAI: `export OPENAI_API_KEY="sk-..."` with `--use-openai`

**Linux/macOS:**
```bash
# Make executable (one time)
chmod +x scripts/sdk-chat.sh

# Generate samples (auto-builds image if not present)
export GH_TOKEN="ghp_..."
./scripts/sdk-chat.sh package sample generate /path/to/sdk

# Force rebuild the image
./scripts/sdk-chat.sh --build package sample generate /path/to/sdk

# With OpenAI instead
export OPENAI_API_KEY="sk-..."
./scripts/sdk-chat.sh package sample generate /path/to/sdk --use-openai

# Other commands
./scripts/sdk-chat.sh doctor
./scripts/sdk-chat.sh --help
```

**Windows PowerShell:**
```powershell
# Generate samples (auto-builds image if not present)
$env:GH_TOKEN = "ghp_..."
.\scripts\sdk-chat.ps1 package sample generate C:\path\to\sdk

# Force rebuild the image
.\scripts\sdk-chat.ps1 --build package sample generate C:\path\to\sdk

# With OpenAI instead
$env:OPENAI_API_KEY = "sk-..."
.\scripts\sdk-chat.ps1 package sample generate C:\path\to\sdk --use-openai
```

### Docker Compose

Alternative approach using Docker Compose:

```bash
# Generate samples
export GH_TOKEN="ghp_..."
SDK_PATH=/path/to/sdk docker compose run --rm sdk-chat package sample generate /sdk

# Start MCP server with SSE on port 8080
docker compose up mcp-sse

# Run interactive ACP agent
SDK_PATH=/path/to/sdk docker compose run --rm acp
```

### All Images

| Image | Dockerfile | Size | Purpose |
|-------|------------|------|--------|
| `sdk-chat-dev` | `Dockerfile` | ~1.5GB | Development/testing |
| `sdk-chat-demo` | `demo/Dockerfile` | ~2GB | Demo recording (VHS) |
| `sdk-chat:latest` | `Dockerfile.release` | ~500MB | Production (Native AOT + glibc-linked extractors) |

The release image includes:
- .NET Native AOT binary (~40MB)
- Go, Java, Python, TypeScript extractors (glibc-linked)
- GitHub Copilot CLI (~138MB)
- distroless/cc-debian12 base (minimal glibc runtime)

```bash
# Build images
docker build -t sdk-chat-dev .                          # Dev
docker build -f demo/Dockerfile -t sdk-chat-demo .      # Demo
docker build -f Dockerfile.release -t sdk-chat:latest . # Production
```

### Manual Docker Usage

If you prefer not to use the wrapper scripts:

**With GitHub token (recommended):**
```bash
docker run --rm \
  -v "/path/to/your-sdk:/sdk" \
  -e GH_TOKEN="ghp_..." \
  sdk-chat:latest package sample generate /sdk --count 5
```

**With Copilot credentials file:**
```bash
docker run --rm \
  -v "$HOME/.copilot:/root/.copilot:ro" \
  -v "/path/to/your-sdk:/sdk" \
  sdk-chat:latest package sample generate /sdk --count 5
```

**With OpenAI:**

```bash
docker run --rm \
  -v "/path/to/your-sdk:/sdk" \
  -e OPENAI_API_KEY="sk-..." \
  sdk-chat:latest package sample generate /sdk --use-openai --count 5
```

**Using a `.env` file:**

```bash
docker run --rm \
  -v "/path/to/your-sdk:/sdk" \
  -v "$PWD/.env:/app/.env:ro" \
  sdk-chat:latest package sample generate /sdk --use-openai --load-dotenv
```

**Run as MCP server (stdio):**

```bash
docker run --rm -i \
  -e GH_TOKEN="ghp_..." \
  sdk-chat:latest mcp
```

**Run as MCP server (SSE on port 8080):**

```bash
docker run --rm -p 8080:8080 \
  -e GH_TOKEN="ghp_..." \
  sdk-chat:latest mcp --transport sse --port 8080
```

Connect to `http://localhost:8080/sse` from your MCP client.

**SSE with OpenAI:**

```bash
docker run --rm -p 8080:8080 \
  -e OPENAI_API_KEY="sk-..." \
  sdk-chat:latest mcp --transport sse --port 8080 --use-openai
```

The container runs as root by default (distroless base). Mount volumes with appropriate permissions.

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
