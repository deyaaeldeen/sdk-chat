# SDK Chat Tool

A command-line interface for SDK sample generation with support for multiple languages and AI-assisted workflows.

## Overview

SDK Chat provides three modes of operation:
- **CLI Mode**: Direct command-line usage for quick sample generation
- **MCP Mode**: Model Context Protocol server for VS Code and Claude Desktop integration
- **ACP Mode**: Agent Client Protocol for interactive AI-assisted workflows

## Supported Languages

| Language | Extractor | Status |
|----------|-----------|--------|
| .NET/C# | Roslyn (embedded) | ✅ Always available |
| Python | Python ast module | ✅ Requires `python3` |
| Java | JBang + JavaParser | ✅ Requires `jbang` |
| Go | go/parser | ✅ Requires `go` |
| TypeScript | ts-morph | ✅ Requires `node` |
| JavaScript | ts-morph | ✅ Requires `node` |

## Installation

### Install as .NET Tool (Recommended)

```bash
# Install globally from local build
dotnet pack Sdk.Tools.Chat/Sdk.Tools.Chat.csproj
dotnet tool install --global --add-source ./artifacts/packages sdk-chat

# Verify installation
sdk-chat --version
```

### Build from Source

```bash
# Build all projects
dotnet build

# Run directly without installing
dotnet run --project Sdk.Tools.Chat/Sdk.Tools.Chat.csproj -- --help
```

## Global Options

These options apply to all commands:

| Option | Description |
|--------|-------------|
| `--use-openai` | Use OpenAI-compatible API instead of GitHub Copilot. Requires `OPENAI_API_KEY` environment variable. |
| `--load-dotenv` | Load environment variables from `.env` in the current directory (best-effort; does not override existing variables). |
| `--help`, `-h`, `-?` | Show help and usage information. |
| `--version` | Show version information. |

## Commands

### `package sample generate` - Generate SDK Samples

Generate samples directly from the command line:

```bash
# Basic usage - auto-detects language
sdk-chat package sample generate /path/to/sdk

# Specify language explicitly
sdk-chat package sample generate /path/to/openai-dotnet --language dotnet

# Specify output directory
sdk-chat package sample generate /path/to/openai-python --output ./samples

# Generate more samples with custom prompt
sdk-chat package sample generate /path/to/sdk --count 10 --prompt "Focus on authentication"

# Preview without writing files
sdk-chat package sample generate /path/to/sdk --dry-run

# Use OpenAI instead of GitHub Copilot
sdk-chat --use-openai package sample generate /path/to/sdk
```

#### SDK Root Path

The `<path>` argument must point to the **SDK root directory** — the top-level folder of the SDK repository, not a subdirectory like `src/` or `lib/`. The tool uses the root to:

- Detect the SDK type and language from project files (`.csproj`, `pyproject.toml`, `pom.xml`, `go.mod`, `package.json`)
- Find the conventional output directory (`samples/`, `examples/`)
- Locate README and other metadata for context

**Correct:**
```bash
# Clone the repo and point to the root
git clone https://github.com/openai/openai-dotnet.git
sdk-chat package sample generate ./openai-dotnet
```

**Incorrect:**
```bash
# Don't point to subdirectories
sdk-chat package sample generate ./openai-dotnet/src  # ❌ Wrong
```

#### Options

| Option | Description | Default |
|--------|-------------|---------|
| `<path>` | Path to SDK root directory — the top-level folder containing project files (`.csproj`, `pyproject.toml`, etc.), not `src/` or other subdirectories | — |
| `--output <dir>` | Output directory for samples | Auto-detected (`samples/`, `examples/`) |
| `--language <lang>` | SDK language: `dotnet`, `python`, `java`, `typescript`, `go` | Auto-detected |
| `--prompt <text>` | Custom generation prompt | — |
| `--count <n>` | Number of samples to generate | `5` |
| `--budget <chars>` | Max context size in characters | `512K` |
| `--model <name>` | AI model to use | `claude-sonnet-4.5` (Copilot), `gpt-5.2` (OpenAI) |
| `--dry-run` | Preview without writing files | `false` |

### `mcp` - Start MCP Server

Start the Model Context Protocol server for AI agent integration:

```bash
# Start with default settings (stdio transport)
sdk-chat mcp

# Use SSE transport on custom port
sdk-chat mcp --transport sse --port 3000

# Enable debug logging
sdk-chat mcp --log-level debug
```

#### Options

| Option | Description | Default |
|--------|-------------|---------|
| `--transport <type>` | Transport type: `stdio`, `sse` | `stdio` |
| `--port <port>` | Port for SSE transport | `8080` |
| `--log-level <level>` | Log level: `debug`, `info`, `warn`, `error` | `info` |

#### VS Code Configuration

Add to your VS Code settings (`settings.json`):

```json
{
  "mcp.servers": {
    "sdk-chat": {
      "command": "sdk-chat",
      "args": ["mcp"]
    }
  }
}
```

Or if running from source:

```json
{
  "mcp.servers": {
    "sdk-chat": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/Sdk.Tools.Chat/Sdk.Tools.Chat.csproj", "--", "mcp"]
    }
  }
}
```

#### Claude Desktop Configuration

Add to your Claude Desktop config (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "sdk-chat": {
      "command": "sdk-chat",
      "args": ["mcp"]
    }
  }
}
```

### `acp` - Start ACP Agent

Start interactive mode with Agent Client Protocol:

```bash
# Start with default settings
sdk-chat acp

# Enable debug logging
sdk-chat acp --log-level debug
```

#### Options

| Option | Description | Default |
|--------|-------------|---------|
| `--log-level <level>` | Log level: `debug`, `info`, `warn`, `error` | `info` |

This mode enables rich, interactive AI-assisted sample generation with:
- Step-by-step guidance
- Real-time feedback
- Permission-based file operations
- Plan visualization
- Streaming token updates

## Example: Generating Samples for OpenAI .NET SDK

```bash
# Clone the OpenAI .NET SDK
git clone https://github.com/openai/openai-dotnet.git

# Generate 3 samples (auto-detects .NET)
sdk-chat package sample generate ./openai-dotnet --count 3

# Generate samples with custom prompt
sdk-chat package sample generate ./openai-dotnet \
  --prompt "Generate samples for chat completions with streaming" \
  --count 5

# Preview what would be generated (dry run)
sdk-chat package sample generate ./openai-dotnet --dry-run

# Use OpenAI API instead of GitHub Copilot
export OPENAI_API_KEY=sk-...
sdk-chat --use-openai package sample generate ./openai-dotnet
```

## API Extractors

SDK Chat includes language-specific API extractors that reduce source code to minimal API surfaces (~95% token reduction), enabling efficient AI processing.

### Standalone Usage

Each extractor can be used independently:

```bash
# C# - extract API from .NET project
dotnet run --project ApiExtractor.DotNet -- /path/to/project --json --pretty

# Python - extract API from Python package  
dotnet run --project ApiExtractor.Python -- /path/to/package --json

# Java - extract API from Java source
dotnet run --project ApiExtractor.Java -- /path/to/src --stub

# Go - extract API from Go module
dotnet run --project ApiExtractor.Go -- /path/to/module --json

# TypeScript - extract API from TypeScript package
dotnet run --project ApiExtractor.TypeScript -- /path/to/package --json --pretty
```

### Extractor CLI Options

All extractors support the same interface:

| Option | Description |
|--------|-------------|
| `<path>` | Path to source directory (required) |
| `--json` | Output as JSON (default: outputs stubs) |
| `--stub` | Output as language-native stubs |
| `--pretty` | Pretty-print JSON with indentation |
| `-o`, `--output <file>` | Write output to file |
| `-h`, `--help` | Show help |

### Output Formats

**JSON Output** - Minimal structured data for AI consumption:
```json
{
  "package": "MyPackage",
  "namespaces": [{
    "name": "MyPackage",
    "types": [{
      "name": "Client",
      "kind": "class",
      "members": [...]
    }]
  }]
}
```

**Stub Output** - Human-readable language-native syntax:
```csharp
namespace MyPackage
{
    public class Client
    {
        public Task<Response> SendAsync(Request request);
    }
}
```

### Programmatic Usage

Extractors implement `IApiExtractor<T>` for easy integration:

```csharp
using ApiExtractor.DotNet;

var extractor = new CSharpApiExtractor();
if (extractor.IsAvailable())
{
    var result = await extractor.ExtractAsync("/path/to/project");
    if (result.IsSuccess)
    {
        Console.WriteLine(extractor.ToJson(result.Value, pretty: true));
    }
}
```

## Architecture

### Agent Client Protocol (ACP) SDK

The `AgentClientProtocol.Sdk` provides a standalone implementation of the Agent Client Protocol:

- **JSON-RPC Layer**: Message serialization and handling
- **ND-JSON Streams**: Newline-delimited JSON transport
- **Connection Management**: Agent and host side connections
- **Schema Definitions**: Standard capability schemas

### Sample Generation

The sample generator:
1. Detects or accepts target language
2. Extracts API surface using language-specific extractor
3. Generates new samples using AI prompts with minimal context
4. Writes files with proper structure

## Project Structure

```
sdk-chat/
├── ApiExtractor.Contracts/      # Shared interface (IApiExtractor<T>)
├── ApiExtractor.DotNet/         # C# extractor using Roslyn
├── ApiExtractor.Python/         # Python extractor using ast
├── ApiExtractor.Java/           # Java extractor using JBang
├── ApiExtractor.Go/             # Go extractor using go/parser
├── ApiExtractor.TypeScript/     # TypeScript extractor using ts-morph
├── ApiExtractor.Tests/          # Test suite (80+ tests)
├── AgentClientProtocol.Sdk/     # ACP SDK implementation
│   ├── Connection/              # Connection handling
│   ├── JsonRpc/                 # JSON-RPC messages
│   ├── Schema/                  # Capability schemas
│   └── Stream/                  # ND-JSON streams
├── Sdk.Tools.Chat/               # Main CLI application
│   ├── Acp/                     # ACP agent host
│   ├── Mcp/                     # MCP server
│   ├── Models/                  # Data models
│   ├── Services/                # Core services
│   │   └── Languages/           # Language-specific handlers
│   └── Tools/                   # CLI tool implementations
└── docs/                        # Documentation
```

## Configuration

### Environment Variables

#### AI Provider Settings

| Variable | Description |
|----------|-------------|
| `OPENAI_API_KEY` | API key for OpenAI-compatible API (required with `--use-openai`) |
| `OPENAI_ENDPOINT` | Base URL for OpenAI-compatible API (optional, for Azure OpenAI or custom endpoints) |
| `SDK_CLI_USE_OPENAI` | Set to `true` to use OpenAI API (alternative to `--use-openai` flag) |
| `SDK_CLI_MODEL` | Override the default AI model |
| `GITHUB_TOKEN` | GitHub token for Copilot API (auto-detected in VS Code) |
| `COPILOT_CLI_PATH` | Path to Copilot CLI executable (default: `copilot`) |

#### Debugging & Diagnostics

| Variable | Description |
|----------|-------------|
| `SDK_CLI_DEBUG` | Set to `true` to enable debug mode (logs prompts/responses) |
| `SDK_CLI_DEBUG_DIR` | Directory for debug output files |
| `SDK_CLI_TIMEOUT` | HTTP timeout in seconds (default: 120) |

#### Telemetry (OpenTelemetry)

| Variable | Description |
|----------|-------------|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | OTLP endpoint for traces (e.g., `http://localhost:4317`) |
| `OTEL_TRACES_EXPORTER` | Trace exporter type: `otlp`, `console`, `none` |
| `SDK_CLI_TELEMETRY_CONSOLE` | Set to `true` to enable console trace output |
| `OTEL_SERVICE_NAME` | Service name for traces (default: `sdk-chat`) |

#### Display

| Variable | Description |
|----------|-------------|
| `NO_COLOR` | Disable colored output (any value) |

### Project Configuration

Create `.sdk-chat.json` in your project root:

```json
{
  "defaultLanguage": "dotnet",
  "samplesDirectory": "./samples",
  "promptsDirectory": "./prompts"
}
```

## Development

### Building

```bash
# Build all projects
dotnet build

# Run tests
dotnet test

# Run specific test projects
dotnet test AgentClientProtocol.Sdk.Tests/AgentClientProtocol.Sdk.Tests.csproj
dotnet test Sdk.Tools.Chat.Tests/Sdk.Tools.Chat.Tests.csproj

# Pack as dotnet tool
dotnet pack Sdk.Tools.Chat/Sdk.Tools.Chat.csproj -o ./artifacts/packages
```

### MCP Schema

The MCP server schema is defined in [Sdk.Tools.Chat/mcp-schema.json](Sdk.Tools.Chat/mcp-schema.json). This schema describes the available tools and capabilities for MCP clients like VS Code and Claude Desktop.

### Adding a New Language

See [CONTRIBUTING.md](CONTRIBUTING.md) for detailed instructions on:
- Implementing `IApiExtractor<T>` interface
- Model conventions (records with init-only properties)
- CLI standardization requirements
- Testing requirements

## License

MIT License - See LICENSE file for details.
