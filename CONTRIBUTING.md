# Contributing to SDK Chat

Welcome! This document provides guidelines for contributing to the SDK Chat project.

## Table of Contents

- [Architecture Overview](#architecture-overview)
- [Project Structure](#project-structure)
- [Adding a New Language Extractor](#adding-a-new-language-extractor)
- [Coding Standards](#coding-standards)
- [Testing](#testing)
- [Building](#building)

## Architecture Overview

The SDK Chat consists of three main components:

### 1. API Extractors

Language-specific tools that extract public API surfaces from source code, producing minimal JSON for AI consumption (~95% token reduction vs. full source).

```
                    ┌─────────────────────────────────────────┐
                    │         IApiExtractor<TIndex>           │
                    │                                         │
                    │  • Language: string                     │
                    │  • IsAvailable(): bool                  │
                    │  • ExtractAsync(path): ExtractorResult  │
                    │  • ToJson(index): string                │
                    │  • ToStubs(index): string               │
                    └─────────────────────────────────────────┘
                                        △
            ┌───────────────────────────┼───────────────────────────┐
            │               │           │           │               │
    ┌───────┴───────┐ ┌─────┴─────┐ ┌───┴───┐ ┌─────┴─────┐ ┌───────┴───────┐
    │ C# (Roslyn)   │ │ Python    │ │ Java  │ │   Go      │ │  TypeScript   │
    │ CSharpApi     │ │ PythonApi │ │ JavaApi│ │ GoApi     │ │ TypeScriptApi │
    │ Extractor     │ │ Extractor │ │Extract│ │ Extractor │ │   Extractor   │
    └───────────────┘ └───────────┘ └───────┘ └───────────┘ └───────────────┘
           │                │           │           │               │
           ▼                ▼           ▼           ▼               ▼
       Roslyn           python3      jbang         go            node.js
       (embedded)       (shell)      (shell)      (shell)        (shell)
```

### 2. Main CLI (`Sdk.Tools.Chat`)

Orchestrates extractors and provides three modes:
- **CLI Mode**: Direct command-line usage
- **MCP Mode**: Model Context Protocol server for VS Code/Claude
- **ACP Mode**: Agent Client Protocol for interactive AI workflows

### 3. Agent Client Protocol SDK

A standalone JSON-RPC implementation for agent communication.

## Project Structure

```
sdk-chat/
├── ApiExtractor.Contracts/      # Shared interface (IApiExtractor<T>)
│   ├── IApiExtractor.cs         # Core interface all extractors implement
│   ├── CliOptions.cs            # Standardized CLI argument parsing
│   └── ExtractorResult.cs       # Success/failure result wrapper
│
├── ApiExtractor.DotNet/         # C# extractor using Roslyn
│   ├── CSharpApiExtractor.cs    # Extractor implementation
│   ├── CSharpFormatter.cs       # Stub output formatter
│   ├── Models.cs                # API data models (records)
│   └── Program.cs               # Standalone CLI entry point
│
├── ApiExtractor.Python/         # Python extractor using ast module
│   ├── PythonApiExtractor.cs    # Shells to python3
│   ├── extract_api.py           # Python parser script
│   ├── PythonFormatter.cs       # Stub output formatter
│   ├── Models.cs                # API data models (records)
│   └── Program.cs               # Standalone CLI entry point
│
├── ApiExtractor.Java/           # Java extractor using JBang + JavaParser
│   ├── JavaApiExtractor.cs      # Shells to jbang
│   ├── ExtractApi.java          # JBang parser script
│   ├── JavaFormatter.cs         # Stub output formatter
│   ├── Models.cs                # API data models (records)
│   └── Program.cs               # Standalone CLI entry point
│
├── ApiExtractor.Go/             # Go extractor using go/parser
│   ├── GoApiExtractor.cs        # Shells to go run
│   ├── extract_api.go           # Go parser script
│   ├── GoFormatter.cs           # Stub output formatter
│   ├── Models.cs                # API data models (records)
│   └── Program.cs               # Standalone CLI entry point
│
├── ApiExtractor.TypeScript/     # TypeScript extractor using ts-morph
│   ├── TypeScriptApiExtractor.cs# Shells to node
│   ├── src/extract_api.ts       # TypeScript parser using ts-morph
│   ├── TypeScriptFormatter.cs   # Stub output formatter
│   ├── Models.cs                # API data models (records)
│   └── Program.cs               # Standalone CLI entry point
│
├── ApiExtractor.Tests/          # Test suite (xUnit)
│   ├── DotNetApiExtractorTests.cs
│   ├── PythonApiExtractorTests.cs
│   ├── JavaApiExtractorTests.cs
│   ├── GoApiExtractorTests.cs
│   ├── TypeScriptApiExtractorTests.cs
│   └── TestFixtures/            # Sample code for each language
│
├── Sdk.Tools.Chat/               # Main CLI application
│   ├── Mcp/                     # MCP server implementation
│   ├── Acp/                     # ACP agent host
│   └── Program.cs               # CLI entry point
│
└── AgentClientProtocol.Sdk/     # Standalone ACP SDK
```

## Adding a New Language Extractor

### Step 1: Create the Project

```bash
cd tools/sdk-chat
dotnet new classlib -n ApiExtractor.NewLang -o ApiExtractor.NewLang
```

Add project reference to the solution and add reference to `ApiExtractor.Contracts`.

### Step 2: Implement `IApiExtractor<T>`

Create your extractor implementing `IApiExtractor<TIndex>`:

```csharp
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using ApiExtractor.Contracts;

namespace ApiExtractor.NewLang;

public class NewLangApiExtractor : IApiExtractor<ApiIndex>
{
    public string Language => "newlang";

    public bool IsAvailable()
    {
        // Check if runtime is available (e.g., interpreter in PATH)
        return true;
    }

    public string? UnavailableReason { get; private set; }

    public async Task<ExtractorResult<ApiIndex>> ExtractAsync(string rootPath, CancellationToken ct)
    {
        // Implement extraction logic
    }

    public string ToJson(ApiIndex index, bool pretty = false) { /* ... */ }
    public string ToStubs(ApiIndex index) { /* ... */ }
}
```

### Step 3: Define Models

Create `Models.cs` using **records with init-only properties**:

```csharp
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace ApiExtractor.NewLang;

public record ApiIndex
{
    [JsonPropertyName("package")]
    public string Package { get; init; } = "";

    [JsonPropertyName("modules")]
    public List<ModuleInfo> Modules { get; init; } = [];
}

// Add other model records...

[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ApiIndex))]
internal partial class SourceGenerationContext : JsonSerializerContext { }
```

### Step 4: Create Formatter

Create a `NewLangFormatter.cs` that outputs human-readable stubs:

```csharp
public static class NewLangFormatter
{
    public static string Format(ApiIndex index) { /* ... */ }
}
```

### Step 5: Create CLI Entry Point

Create `Program.cs` using the shared `CliOptions`:

```csharp
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using ApiExtractor.Contracts;
using ApiExtractor.NewLang;

var options = CliOptions.Parse(args);

if (options.ShowHelp || options.Path == null)
{
    Console.WriteLine(CliOptions.GetHelpText("NewLang", "ApiExtractor.NewLang"));
    return options.ShowHelp ? 0 : 1;
}

// ... standard CLI implementation
```

### Step 6: Add Tests

Create `NewLangApiExtractorTests.cs` in `ApiExtractor.Tests/`:

- Add test fixtures in `TestFixtures/NewLang/`
- Follow existing test patterns
- Use `[SkippableFact]` for tests requiring runtime

## Coding Standards

### C# Style

- **Target Framework**: .NET 10.0
- **Records**: Use records with `{ get; init; }` for model classes
- **Namespaces**: Use file-scoped namespaces (`namespace Foo;`)
- **Nullable**: Nullable reference types enabled
- **Collection expressions**: Use `[]` instead of `new List<T>()`
- **Copyright header**: Required on all `.cs` files:

```csharp
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
```

### JSON Serialization

- Use `[JsonPropertyName]` attributes (camelCase)
- Use `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]` for nullable fields
- Add `JsonSerializerContext` for AOT compatibility

### CLI Interface

All extractors must support these standardized options:

```
<extractor> <path> [options]

Options:
  --json      Output as JSON (default: outputs stubs)
  --stub      Output as language-native stubs
  --pretty    Pretty-print JSON with indentation
  -o, --output <file>  Write output to file
  -h, --help  Show help
```

### Exit Codes

- `0`: Success
- `1`: Error (with message to stderr)

## Testing

### Run All Tests

```bash
cd tools/sdk-chat
dotnet test ApiExtractor.Tests
```

### Run Specific Extractor Tests

```bash
dotnet test ApiExtractor.Tests --filter "FullyQualifiedName~DotNetApiExtractor"
```

### Test Fixtures

Each language has test fixtures in `ApiExtractor.Tests/TestFixtures/<Language>/`:
- Include representative code samples
- Cover classes, interfaces, enums, functions
- Include edge cases (generics, async, etc.)

## Building

### Build All Projects

```bash
cd tools/sdk-chat
dotnet build
```

### Build and Run a Single Extractor

```bash
dotnet run --project ApiExtractor.DotNet -- /path/to/code --json --pretty
```

### Publish Self-Contained

```bash
dotnet publish Sdk.Tools.Chat -c Release -r linux-x64 --self-contained
```

## Questions?

Open an issue in the repository or reach out to the maintainers.
