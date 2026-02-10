# SDK Chat

**Generate production-ready SDK samples in seconds using AI.**

![SDK Chat Demo](demo/demo.gif)

```bash
dotnet run --project src/Microsoft.SdkChat -- package samples generate /path/to/sdk
```

## Install

```bash
git clone https://github.com/deyaaeldeen/sdk-chat && cd sdk-chat
```

**Requires:** [.NET SDK 10.0+](https://dot.net) + GitHub token (`GH_TOKEN`) or OpenAI key (`OPENAI_API_KEY`)

## Quick Start

```bash
export GH_TOKEN="ghp_..."
dotnet run --project src/Microsoft.SdkChat -- package samples generate /path/to/openai-dotnet
```

```bash
# Custom prompt + count
dotnet run --project src/Microsoft.SdkChat -- package samples generate /path/to/sdk --count 10 --prompt "streaming examples"

# Preview without writing
dotnet run --project src/Microsoft.SdkChat -- package samples generate /path/to/sdk --dry-run

# Use OpenAI instead
export OPENAI_API_KEY="sk-..."
dotnet run --project src/Microsoft.SdkChat -- package samples generate /path/to/sdk --use-openai
```

## Commands

| Command | Purpose |
|---------|---------|
| `package samples generate <path>` | Generate samples with AI |
| `package samples detect <path>` | Find existing samples folder |
| `package api extract <path>` | Extract public API surface |
| `package api coverage <path>` | Find documentation gaps |
| `package source detect <path>` | Detect language + source folder |

### `package samples generate`

| Option | Default | Description |
|--------|---------|-------------|
| `--count <n>` | `5` | Number of samples |
| `--prompt <text>` | — | Guide generation: `"streaming"`, `"error handling"` |
| `--output <dir>` | Auto | Output directory |
| `--model <name>` | `claude-sonnet-4.5` | AI model |
| `--budget <chars>` | `512K` | Max context size |
| `--dry-run` | `false` | Preview only |
| `--use-openai` | `false` | Use OpenAI API |

### `package api coverage`

| Option | Default | Description |
|--------|---------|-------------|
| `--samples <dir>` | Auto | Samples folder path |
| `--uncovered-only` | `false` | Show only gaps |
| `--json` | `false` | JSON output |
| `--monorepo` | `false` | Analyze all packages in a monorepo |
| `--report <file>` | stdout | Write Markdown report to file (monorepo only) |
| `--quiet` | `false` | Suppress progress output |
| `--skip-empty` | `false` | Omit packages with 0 operations (monorepo only) |

### `package api extract`

| Option | Default | Description |
|--------|---------|-------------|
| `--json` | `false` | Structured JSON output |
| `--output <file>` | stdout | Write to file |

## MCP Server

Expose SDK Chat to AI agents (VS Code Copilot, Claude Desktop).

```bash
dotnet run --project src/Microsoft.SdkChat -- mcp
```

### VS Code Setup

Add to `.vscode/mcp.json`:

```json
{
  "servers": {
    "sdk-chat": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "${workspaceFolder}/src/Microsoft.SdkChat", "--", "mcp"]
    }
  }
}
```

### Claude Desktop Setup

Add to `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "sdk-chat": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/sdk-chat/src/Microsoft.SdkChat", "--", "mcp"]
    }
  }
}
```

### MCP Tools

| Tool | Purpose |
|------|---------|
| `build_samples_prompt` | Build AI prompt for sample generation |
| `validate_samples` | Parse and validate AI-generated samples |
| `analyze_coverage` | Find documentation gaps |
| `extract_api` | Get public API signatures |
| `detect_source` | Verify SDK is recognized |
| `detect_samples` | Find existing samples |

## Supported Languages

.NET/C# • Python • TypeScript • JavaScript • Java • Go

Auto-detected from project files.

---

**More:** [Configuration](docs/configuration.md) • [Contributing](CONTRIBUTING.md)

## License

MIT
