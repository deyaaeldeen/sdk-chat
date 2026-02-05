# SDK Chat

**Generate production-ready SDK samples in seconds using AI.**

![SDK Chat Demo](demo/demo.gif)

```bash
./scripts/sdk-chat.sh package samples generate /path/to/sdk
```

## Install

```bash
git clone https://github.com/deyaaeldeen/sdk-chat && cd sdk-chat
docker build -f Dockerfile.release -t sdk-chat:latest .
chmod +x scripts/sdk-chat.sh
```

**Requires:** [Docker](https://docs.docker.com/get-docker/) + GitHub token (`GH_TOKEN`) or OpenAI key (`OPENAI_API_KEY`)

## Quick Start

```bash
export GH_TOKEN="ghp_..."
./scripts/sdk-chat.sh package samples generate /path/to/openai-dotnet
```

```bash
# Custom prompt + count
./scripts/sdk-chat.sh package samples generate /path/to/sdk --count 10 --prompt "streaming examples"

# Preview without writing
./scripts/sdk-chat.sh package samples generate /path/to/sdk --dry-run

# Use OpenAI instead
export OPENAI_API_KEY="sk-..."
./scripts/sdk-chat.sh package samples generate /path/to/sdk --use-openai
```

**Windows PowerShell:** Use `.\scripts\sdk-chat.ps1` instead.

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
./scripts/sdk-chat.sh mcp
```

### VS Code Setup

Add to `.vscode/mcp.json`:

```json
{
  "servers": {
    "sdk-chat": {
      "type": "stdio",
      "command": "${workspaceFolder}/scripts/sdk-chat.sh",
      "args": ["mcp"],
      "env": { "SDK_WORKSPACE": "${workspaceFolder}" }
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
      "command": "bash",
      "args": ["-c", "docker run --rm -i -u $(id -u):$(id -g) -v $HOME:$HOME -e HOME=$HOME sdk-chat:latest mcp"]
    }
  }
}
```

### MCP Tools

| Tool | Purpose |
|------|---------|
| `generate_samples` | Create samples with AI |
| `analyze_coverage` | Find documentation gaps |
| `extract_api` | Get public API signatures |
| `detect_source` | Verify SDK is recognized |
| `detect_samples` | Find existing samples |

## Supported Languages

.NET/C# • Python • TypeScript • JavaScript • Java • Go

Auto-detected from project files. All extractors included in Docker image.

---

**More:** [Configuration](docs/configuration.md) • [Docker](docs/docker.md) • [Contributing](CONTRIBUTING.md)

## License

MIT
