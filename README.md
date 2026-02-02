# SDK Chat

**Generate production-ready SDK samples in seconds using AI.**

![SDK Chat Demo](demo/demo.gif)

```bash
./scripts/sdk-chat.sh package sample generate /path/to/sdk
```

## Installation

SDK Chat runs in Docker to ensure consistent behavior across all platforms with all language extractors pre-configured.

### Prerequisites

- [Docker](https://docs.docker.com/get-docker/) (required)
- One of the following for AI authentication:
  - GitHub token (`GH_TOKEN` or `GITHUB_TOKEN`) for GitHub Copilot
  - OpenAI API key (`OPENAI_API_KEY`) with `--use-openai` flag

### Setup

```bash
# Clone the repository
git clone https://github.com/deyaaeldeen/sdk-chat
cd sdk-chat

# Build the Docker image (one time)
docker build -f Dockerfile.release -t sdk-chat:latest .

# Make wrapper script executable (Linux/macOS)
chmod +x scripts/sdk-chat.sh
```

The wrapper scripts (`scripts/sdk-chat.sh` for Linux/macOS, `scripts/sdk-chat.ps1` for Windows) handle volume mounts and environment variables automatically.

## Quick Start

**Linux/macOS:**
```bash
# Set your authentication
export GH_TOKEN="ghp_..."

# Generate 5 samples (default)
./scripts/sdk-chat.sh package sample generate /path/to/openai-dotnet

# Use OpenAI instead of GitHub Copilot
export OPENAI_API_KEY="sk-..."
./scripts/sdk-chat.sh package sample generate /path/to/sdk --use-openai

# Custom count + prompt
./scripts/sdk-chat.sh package sample generate /path/to/sdk --count 10 --prompt "streaming examples"

# Preview without writing
./scripts/sdk-chat.sh package sample generate /path/to/sdk --dry-run
```

**Windows PowerShell:**
```powershell
# Set your authentication
$env:GH_TOKEN = "ghp_..."

# Generate samples
.\scripts\sdk-chat.ps1 package sample generate C:\path\to\sdk
```

## Supported Languages

All language extractors are included in the Docker image with no additional setup required.

| Language | Detection | Extractor |
|----------|-----------|-----------|
| .NET/C# | `.csproj`, `.sln` | Roslyn |
| Python | `pyproject.toml`, `setup.py` | ast |
| TypeScript | `package.json` + `.ts` | ts-morph |
| JavaScript | `package.json` | ts-morph |
| Java | `pom.xml`, `build.gradle` | JavaParser |
| Go | `go.mod` | go/parser |

---

## Commands

### `package sample generate`

Generate code samples for an SDK.

```bash
./scripts/sdk-chat.sh package sample generate <path> [options]
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
./scripts/sdk-chat.sh mcp [options]
```

| Option | Default | Description |
|--------|---------|-------------|
| `--transport <type>` | `stdio` | Transport: `stdio` or `sse` |
| `--port <n>` | `8080` | Port for SSE transport |
| `--log-level <level>` | `info` | Logging verbosity |
| `--use-openai` | `false` | Use OpenAI API |
| `--load-dotenv` | `false` | Load `.env` file |

**VS Code MCP** (`settings.json`) - using Docker:
```json
{
  "mcp.servers": {
    "sdk-chat": {
      "command": "docker",
      "args": ["run", "--rm", "-i", "-e", "GH_TOKEN", "sdk-chat:latest", "mcp"]
    }
  }
}
```

**Claude Desktop** (`claude_desktop_config.json`) - using Docker:
```json
{
  "mcpServers": {
    "sdk-chat": {
      "command": "docker",
      "args": ["run", "--rm", "-i", "-e", "GH_TOKEN", "sdk-chat:latest", "mcp"]
    }
  }
}
```

### `acp`

Start interactive agent for guided sample generation.

```bash
./scripts/sdk-chat.sh acp [options]
```

| Option | Default | Description |
|--------|---------|-------------|
| `--log-level <level>` | `info` | Logging verbosity |
| `--use-openai` | `false` | Use OpenAI API |
| `--load-dotenv` | `false` | Load `.env` file |

### `doctor`

Validate external dependencies.

```bash
./scripts/sdk-chat.sh doctor
```

---

## Wrapper Scripts

The wrapper scripts are the recommended way to run SDK Chat. They handle Docker volume mounts, environment variable passthrough, and authentication automatically.

| Script | Platform | Location |
|--------|----------|----------|
| `sdk-chat.sh` | Linux/macOS | `scripts/sdk-chat.sh` |
| `sdk-chat.ps1` | Windows | `scripts/sdk-chat.ps1` |

### Authentication Options

Choose one of the following:

| Method | Environment Variable | Flag |
|--------|---------------------|------|
| GitHub Copilot | `GH_TOKEN` or `GITHUB_TOKEN` | (default) |
| Copilot credentials | Auto-mounted from `~/.copilot` | (default) |
| OpenAI | `OPENAI_API_KEY` | `--use-openai` |

### Rebuilding the Image

```bash
# Force rebuild with --build flag
./scripts/sdk-chat.sh --build package sample generate /path/to/sdk

# Or rebuild manually
docker build -f Dockerfile.release -t sdk-chat:latest .
```

---

## Docker Compose

Alternative approach for complex workflows:

```bash
# Generate samples
export GH_TOKEN="ghp_..."
SDK_PATH=/path/to/sdk docker compose run --rm sdk-chat package sample generate /sdk

# Start MCP server with SSE on port 8080
docker compose up mcp-sse

# Run interactive ACP agent
SDK_PATH=/path/to/sdk docker compose run --rm acp
```

---

## Advanced Docker Usage

For users who prefer direct Docker commands without wrapper scripts.

### With GitHub Token (Recommended)

```bash
docker run --rm \
  -v "/path/to/your-sdk:/sdk" \
  -e GH_TOKEN="ghp_..." \
  sdk-chat:latest package sample generate /sdk --count 5
```

### With Copilot Credentials

```bash
docker run --rm \
  -v "$HOME/.copilot:/root/.copilot:ro" \
  -v "/path/to/your-sdk:/sdk" \
  sdk-chat:latest package sample generate /sdk --count 5
```

### With OpenAI

```bash
docker run --rm \
  -v "/path/to/your-sdk:/sdk" \
  -e OPENAI_API_KEY="sk-..." \
  sdk-chat:latest package sample generate /sdk --use-openai --count 5
```

### Using a .env File

```bash
docker run --rm \
  -v "/path/to/your-sdk:/sdk" \
  -v "$PWD/.env:/app/.env:ro" \
  sdk-chat:latest package sample generate /sdk --use-openai --load-dotenv
```

### MCP Server (stdio)

```bash
docker run --rm -i \
  -e GH_TOKEN="ghp_..." \
  sdk-chat:latest mcp
```

### MCP Server (SSE)

```bash
docker run --rm -p 8080:8080 \
  -e GH_TOKEN="ghp_..." \
  sdk-chat:latest mcp --transport sse --port 8080
```

Connect to `http://localhost:8080/sse` from your MCP client.

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

## Docker Images

| Image | Dockerfile | Size | Purpose |
|-------|------------|------|--------|
| `sdk-chat:latest` | `Dockerfile.release` | ~500MB | Production (Native AOT) |
| `sdk-chat-dev` | `Dockerfile` | ~1.5GB | Development/testing |
| `sdk-chat-demo` | `demo/Dockerfile` | ~2GB | Demo recording (VHS) |

The release image includes:
- .NET Native AOT binary (~40MB)
- Go, Java, Python, TypeScript extractors (glibc-linked)
- GitHub Copilot CLI (~138MB)
- distroless/cc-debian12 base (minimal glibc runtime)

```bash
# Build images
docker build -f Dockerfile.release -t sdk-chat:latest . # Production (default)
docker build -t sdk-chat-dev .                          # Dev
docker build -f demo/Dockerfile -t sdk-chat-demo .      # Demo
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

## Alternative Installation

### .NET Global Tool

For users who prefer not to use Docker and have all language runtimes installed locally:

```bash
dotnet tool install --global Microsoft.SdkChat
```

**Requirements for non-Docker installation:**
- .NET 10 SDK
- Python 3 (for Python SDK extraction)
- Node.js (for TypeScript/JavaScript SDK extraction)
- Go (for Go SDK extraction)
- JBang (for Java SDK extraction)

### Build from Source

```bash
git clone https://github.com/deyaaeldeen/sdk-chat
cd sdk-chat
dotnet build
dotnet run --project src/Microsoft.SdkChat -- package sample generate /path/to/sdk
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
