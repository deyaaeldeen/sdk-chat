# Contributing

## Quick Links

| I want to... | Go to |
|--------------|-------|
| Run tests | [Testing](#testing) |
| Add a new language extractor | [Adding a Language](#adding-a-language) |
| Understand the architecture | [Architecture](#architecture) |
| Follow code style | [Coding Standards](#coding-standards) |
| Build the project | [Building](#building) |

---

## Getting Started

```bash
git clone https://github.com/deyaaeldeen/sdk-chat
cd sdk-chat
dotnet build
dotnet test
```

### Using Docker

A unified development container includes all language runtimes and tools:

```bash
# Build container
docker build -t sdk-chat-dev .

# Run tests
docker run --rm -v "$(pwd):/workspace" sdk-chat-dev

# Interactive shell
docker run -it --rm -v "$(pwd):/workspace" sdk-chat-dev bash

# Build only
docker run --rm -v "$(pwd):/workspace" sdk-chat-dev dotnet build
```

### VS Code Dev Container

Open in container: F1 → "Dev Containers: Reopen in Container"

The dev container includes: .NET SDK 10, Python 3, Node.js, Go, JBang, VHS, Copilot CLI.

---

## Project Structure

```
sdk-chat/
├── src/
│   ├── Microsoft.SdkChat/              # Main CLI tool
│   ├── AgentClientProtocol.Sdk/        # ACP protocol implementation
│   ├── AgentClientProtocol.Sdk.Generators/  # Source generator
│   ├── ApiExtractor.Contracts/         # Shared interfaces
│   ├── ApiExtractor.DotNet/            # C# extractor (Roslyn)
│   ├── ApiExtractor.Python/            # Python extractor (ast)
│   ├── ApiExtractor.TypeScript/        # TypeScript extractor (ts-morph)
│   ├── ApiExtractor.Java/              # Java extractor (JavaParser)
│   └── ApiExtractor.Go/                # Go extractor (go/parser)
├── tests/
│   ├── Microsoft.SdkChat.Tests/        # CLI + service tests (270+)
│   ├── AgentClientProtocol.Sdk.Tests/  # Protocol tests (70+)
│   └── ApiExtractor.Tests/             # Extractor tests (140+)
├── demo/                               # Demo recording
└── docs/                               # Documentation
```

---

## Testing

### Run All Tests

```bash
dotnet test  # 480+ tests
```

### Run by Project

```bash
# CLI and services
dotnet test tests/Microsoft.SdkChat.Tests

# Protocol
dotnet test tests/AgentClientProtocol.Sdk.Tests

# Extractors
dotnet test tests/ApiExtractor.Tests
```

### Run by Filter

```bash
# By class name
dotnet test --filter "FullyQualifiedName~DotNetApiExtractor"

# By test name
dotnet test --filter "DisplayName~streaming"

# Multiple filters
dotnet test --filter "FullyQualifiedName~Python|FullyQualifiedName~Java"
```

### Skippable Tests

Some tests require external tools (python3, node, jbang, go). They auto-skip if unavailable:

```csharp
[SkippableFact]
public async Task ExtractsApi()
{
    Skip.IfNot(_extractor.IsAvailable(), "python3 not installed");
    // ...
}
```

### Test Fixtures

Located in `tests/ApiExtractor.Tests/TestFixtures/<Language>/`:
- Minimal but representative code samples
- Cover classes, interfaces, enums, generics
- Used by all extractor tests

### Writing Tests

```csharp
public class MyFeatureTests
{
    [Fact]
    public async Task Feature_Condition_Expected()
    {
        // Arrange
        var sut = new MyService();
        
        // Act
        var result = await sut.DoThingAsync();
        
        // Assert
        Assert.NotNull(result);
    }
}
```

---

## Architecture

```
+---------------------------------------------------------------+
|                       Microsoft.SdkChat                       |
|                                                               |
|   +-------+       +-------+       +-----------------------+   |
|   |  CLI  |       |  MCP  |       |          ACP          |   |
|   | Mode  |       |Server |       |   Interactive Mode    |   |
|   +---+---+       +---+---+       +-----------+-----------+   |
|       |               |                       |               |
|       +---------------+-----------+-----------+               |
|                                   |                           |
|                       +-----------v-----------+               |
|                       |   Sample Generator    |               |
|                       |    + AI Service       |               |
|                       +-----------+-----------+               |
+-----------------------+-----------+---------------------------+
                                    |
        +----------+----------+-----+-----+----------+----------+
        v          v          v           v          v          v
   +-------+  +-------+  +-------+  +-------+  +-----------+
   | .NET  |  |Python |  | Java  |  |  Go   |  |TypeScript |
   |Roslyn |  |  ast  |  |Parser |  |parser |  | ts-morph  |
   +-------+  +-------+  +-------+  +-------+  +-----------+
```

### Components

| Component | Purpose |
|-----------|---------|
| **Microsoft.SdkChat** | Main CLI with three modes (CLI, MCP, ACP) |
| **AgentClientProtocol.Sdk** | Standalone ACP protocol implementation |
| **ApiExtractor.\*** | Language-specific API extractors |
| **ApiExtractor.Contracts** | Shared `IApiExtractor<T>` interface |

---

## Adding a Language

### 1. Create Project

```bash
dotnet new classlib -n ApiExtractor.NewLang -o src/ApiExtractor.NewLang
dotnet sln add src/ApiExtractor.NewLang
```

Add reference to `src/ApiExtractor.NewLang/ApiExtractor.NewLang.csproj`:
```xml
<ProjectReference Include="..\ApiExtractor.Contracts\ApiExtractor.Contracts.csproj" />
```

### 2. Implement Interface

```csharp
// ApiExtractor.NewLang/NewLangApiExtractor.cs
public class NewLangApiExtractor : IApiExtractor<ApiIndex>
{
    public string Language => "newlang";
    
    public bool IsAvailable()
    {
        // Check if runtime exists (e.g., `newlang --version`)
        return ProcessHelper.TryRun("newlang", "--version");
    }
    
    public string? UnavailableReason { get; private set; }
    
    public async Task<ExtractorResult<ApiIndex>> ExtractAsync(
        string rootPath, 
        CancellationToken ct)
    {
        // Shell to language parser script or use embedded parser
    }
    
    public string ToJson(ApiIndex index, bool pretty = false) => /* ... */;
    public string ToStubs(ApiIndex index) => /* ... */;
}
```

### 3. Define Models

```csharp
// ApiExtractor.NewLang/Models.cs
public record ApiIndex
{
    [JsonPropertyName("package")]
    public string Package { get; init; } = "";
    
    [JsonPropertyName("modules")]
    public List<ModuleInfo> Modules { get; init; } = [];
}
```

Use **records** with `{ get; init; }` properties.

### 4. Create Formatter

```csharp
// ApiExtractor.NewLang/NewLangFormatter.cs
public static class NewLangFormatter
{
    public static string Format(ApiIndex index)
    {
        // Return language-native stub syntax
    }
}
```

### 5. Add CLI Entry Point

```csharp
// ApiExtractor.NewLang/Program.cs
var options = CliOptions.Parse(args);
if (options.ShowHelp || options.Path == null)
{
    Console.WriteLine(CliOptions.GetHelpText("NewLang", "ApiExtractor.NewLang"));
    return options.ShowHelp ? 0 : 1;
}
// Standard extraction flow...
```

### 6. Add Tests

Create `tests/ApiExtractor.Tests/NewLangApiExtractorTests.cs`:

```csharp
public class NewLangApiExtractorTests
{
    [SkippableFact]
    public async Task ExtractsPublicApi()
    {
        Skip.IfNot(_extractor.IsAvailable(), "newlang not installed");
        // Test extraction
    }
}
```

Add fixtures in `tests/ApiExtractor.Tests/TestFixtures/NewLang/`.

### 7. Register in Main Tool

Update `src/Microsoft.SdkChat/Services/Languages/LanguageDetector.cs` to detect the new language.

---

## Coding Standards

### Required

| Rule | Example |
|------|---------|
| .NET 10 | `<TargetFramework>net10.0</TargetFramework>` |
| File-scoped namespaces | `namespace Foo;` |
| Records for models | `public record Foo { get; init; }` |
| Collection expressions | `[]` not `new List<T>()` |
| Nullable enabled | `<Nullable>enable</Nullable>` |
| Copyright header | Every `.cs` file |

### JSON Serialization

```csharp
[JsonPropertyName("camelCase")]
public string Property { get; init; }

[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public string? OptionalProperty { get; init; }
```

### CLI Interface

All extractors must support:
```
<extractor> <path> [--json] [--stub] [--pretty] [-o <file>] [-h]
```

Exit codes: `0` success, `1` error.

---

## Building

```bash
# Build all
dotnet build

# Run CLI directly
dotnet run --project src/Microsoft.SdkChat -- package sample generate /path/to/sdk

# Run single extractor
dotnet run --project src/ApiExtractor.DotNet -- /path --json --pretty

# Pack as tool
dotnet pack src/Microsoft.SdkChat -o ./artifacts

# Install locally
dotnet tool install --global --add-source ./artifacts Microsoft.SdkChat
```

---

## Pull Request Checklist

- [ ] `dotnet build` passes
- [ ] `dotnet test` passes (or new tests skip appropriately)
- [ ] New code has tests
- [ ] Follows coding standards below
- [ ] Updated relevant README if adding features

---

## Questions?

Open an issue or check existing discussions.
