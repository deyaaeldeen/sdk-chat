# AGENTS.md

Instructions for AI coding agents working on this codebase.

## Project

SDK Chat - CLI tool for generating SDK code samples using AI. .NET 10, C#.

## Development Container

**Use the Docker container for all development and testing.** This ensures consistent environments with all dependencies (Python, Node.js, Go, JBang).

```bash
# Build dev container
docker build -t sdk-chat-dev .

# Run tests (recommended - ensures all extractors work)
docker run --rm -u $(id -u):$(id -g) -v "$(pwd):/workspace" sdk-chat-dev

# Interactive development shell
docker run -it --rm -u $(id -u):$(id -g) -v "$(pwd):/workspace" sdk-chat-dev bash

# Build only
docker run --rm -u $(id -u):$(id -g) -v "$(pwd):/workspace" sdk-chat-dev dotnet build

# Run specific test
docker run --rm -u $(id -u):$(id -g) -v "$(pwd):/workspace" sdk-chat-dev dotnet test --filter "FullyQualifiedName~AiServiceTests"
```

> **Note:** The `-u $(id -u):$(id -g)` flag maps your host user into the container, ensuring files created in `/workspace` have correct ownership.

### Docker Images

| Image | Dockerfile | Purpose |
|-------|------------|--------|
| `sdk-chat-dev` | `Dockerfile` | Development, testing |
| `sdk-chat-demo` | `demo/Dockerfile` | VHS demo recording |
| `sdk-chat:latest` | `Dockerfile.release` | Production (Native AOT, ~500MB) |

### AOT Publishing

The release Dockerfile produces a native AOT binary with glibc-linked extractors:

```bash
docker build -f Dockerfile.release -t sdk-chat:latest .

# Test it
docker run --rm sdk-chat:latest --help

# Generate samples with GitHub token (recommended)
# Note: -u flag ensures correct file ownership
docker run --rm -u $(id -u):$(id -g) \
  -e GH_TOKEN="ghp_..." \
  -v "$HOME:$HOME" \
  -e "HOME=$HOME" \
  sdk-chat:latest package samples generate /path/to/sdk

# Or with Docker Compose
GH_TOKEN="ghp_..." SDK_PATH=/path/to/sdk docker compose run --rm sdk-chat package samples generate /sdk
```

### Wrapper Scripts (Host Only)

The wrapper scripts in `scripts/` are designed for use **on the host machine**, not inside containers. They handle:
- User ID mapping (`-u $(id -u):$(id -g)`) for correct file permissions
- Home directory mounting for path transparency
- Copilot credentials mounting at user's home (not `/root`)
- Workspace mounting for MCP via `SDK_WORKSPACE` env var

```bash
# From host machine (not inside dev container)
./scripts/sdk-chat.sh package samples generate /path/to/sdk
```

### Docker-in-Docker (Dev Container)

When testing the release container from inside the dev container, you must use **host paths**, not container paths:

```bash
# Inside dev container, paths are mapped:
#   Container: /workspaces/sdk-chat → Host: /home/<user>/sdk-chat

# Find your host path
docker inspect $(hostname) | grep -A 2 'workspaces/sdk-chat'

# Use the HOST path when mounting volumes
docker run --rm \
  -e GH_TOKEN="ghp_..." \
  -v "/home/<user>/sdk-chat/temp/openai-dotnet:/sdk" \
  sdk-chat:latest package samples generate /sdk --dry-run
```

> **Why?** The Docker socket is shared, so `docker run` commands execute on the host. Paths passed to `-v` must exist on the host, not inside the dev container.

## Structure

```
src/                        # Source code
  Microsoft.SdkChat/        # Main CLI (entry point: Program.cs)
  AgentClientProtocol.Sdk/  # ACP protocol library
  ApiExtractor.*/           # Language-specific API extractors
tests/                      # Test projects (xUnit)
demo/                       # Demo recording
```

## Build & Test

Inside container (preferred):
```bash
dotnet build               # Build all
dotnet test                # Run all tests (480+)
dotnet test tests/Microsoft.SdkChat.Tests  # Run specific project
```

## Code Patterns

### Required in all .cs files

```csharp
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
```

### Style

- File-scoped namespaces: `namespace Foo;`
- Records for models: `public record Foo { get; init; }`
- Collection expressions: `[]` not `new List<T>()`
- Nullable enabled everywhere
- `async`/`await` for I/O operations

### JSON

```csharp
[JsonPropertyName("camelCase")]
public string Property { get; init; } = "";

[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public string? OptionalProperty { get; init; }
```

## Key Files

| Task | File |
|------|------|
| CLI commands | `src/Microsoft.SdkChat/Program.cs` |
| Package info service | `src/Microsoft.SdkChat/Services/PackageInfoService.cs` |
| Sample generator service | `src/Microsoft.SdkChat/Services/SampleGeneratorService.cs` |
| AI streaming | `src/Microsoft.SdkChat/Services/AiService.cs` |
| Sample generation (CLI) | `src/Microsoft.SdkChat/Tools/Package/Samples/SampleGeneratorTool.cs` |
| Language detection | `src/Microsoft.SdkChat/Services/SdkInfo.cs` |
| MCP server | `src/Microsoft.SdkChat/Mcp/McpServer.cs` |
| MCP source tools | `src/Microsoft.SdkChat/Mcp/SourceMcpTools.cs` |
| MCP samples tools | `src/Microsoft.SdkChat/Mcp/SamplesMcpTools.cs` |
| MCP API tools | `src/Microsoft.SdkChat/Mcp/ApiMcpTools.cs` |
| ACP agent | `src/Microsoft.SdkChat/Acp/SampleGeneratorAgent.cs` |
| Entity commands | `src/Microsoft.SdkChat/Commands/*EntityCommand.cs` |

## CLI Structure

Commands follow entity-based structure: `package <entity> <action>`

```
package source detect <path>     # Detect source folder + language
package samples detect <path>    # Detect samples folder
package samples generate <path>  # Generate samples (AI-powered)
package api extract <path>       # Extract public API surface
package api coverage <path>      # Analyze coverage gaps
```

## Adding Features

1. Add implementation in `src/`
2. Add tests in corresponding `tests/` project
3. Run `dotnet test` before committing
4. Update README if adding CLI options or env vars

## Test Commands

```bash
# Run all tests (inside container)
dotnet test

# Run specific test class
dotnet test --filter "FullyQualifiedName~AiServiceTests"

# Run tests for a feature
dotnet test --filter "DisplayName~streaming"

# Run from host (uses container)
docker run --rm -u $(id -u):$(id -g) -v "$(pwd):/workspace" sdk-chat-dev dotnet test
```

## Do Not

- Modify `*.csproj` target frameworks without explicit request
- Add new NuGet packages without explicit request
- Change public API signatures in AgentClientProtocol.Sdk (breaking change)
- Remove copyright headers
- Run tests outside Docker if Python/Node/Go tests fail (container has all deps)
