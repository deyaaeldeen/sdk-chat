# AGENTS.md

Instructions for AI coding agents working on this codebase.

## Project

SDK Chat - CLI tool for generating SDK code samples using AI. .NET 10, C#.

## Development Container

**Use the Docker container for all development and testing.** This ensures consistent environments with all dependencies (Python, Node.js, Go, JBang).

```bash
# Build container (first time or after Dockerfile changes)
docker build -t sdk-chat-dev .

# Run tests (recommended - ensures all extractors work)
docker run --rm -v "$(pwd):/workspace" sdk-chat-dev

# Interactive development shell
docker run -it --rm -v "$(pwd):/workspace" sdk-chat-dev bash

# Build only
docker run --rm -v "$(pwd):/workspace" sdk-chat-dev dotnet build

# Run specific test
docker run --rm -v "$(pwd):/workspace" sdk-chat-dev dotnet test --filter "FullyQualifiedName~AiServiceTests"
```

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
| AI streaming | `src/Microsoft.SdkChat/Services/AiService.cs` |
| Sample generation | `src/Microsoft.SdkChat/Tools/Package/Samples/SampleGeneratorTool.cs` |
| Language detection | `src/Microsoft.SdkChat/Services/SdkInfo.cs` |
| MCP server | `src/Microsoft.SdkChat/Mcp/McpServer.cs` |
| ACP agent | `src/Microsoft.SdkChat/Acp/SampleGeneratorAgent.cs` |

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
docker run --rm -v "$(pwd):/workspace" sdk-chat-dev dotnet test
```

## Do Not

- Modify `*.csproj` target frameworks without explicit request
- Add new NuGet packages without explicit request
- Change public API signatures in AgentClientProtocol.Sdk (breaking change)
- Remove copyright headers
- Run tests outside Docker if Python/Node/Go tests fail (container has all deps)
