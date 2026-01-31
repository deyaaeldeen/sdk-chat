# Contributing

## Quick Links

| I want to... | Go to |
|--------------|-------|
| Add a new language extractor | [Adding a Language](#adding-a-language) |
| Understand the architecture | [Architecture](#architecture) |
| Run tests | [Testing](#testing) |
| Follow code style | [Standards](#coding-standards) |

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                        Microsoft.SdkChat                        │
│                                                                 │
│   ┌─────────┐     ┌─────────┐     ┌─────────────────────────┐  │
│   │   CLI   │     │   MCP   │     │          ACP            │  │
│   │  Mode   │     │ Server  │     │    Interactive Mode     │  │
│   └────┬────┘     └────┬────┘     └────────────┬────────────┘  │
│        │               │                       │               │
│        └───────────────┴───────────┬───────────┘               │
│                                    │                           │
│                         ┌──────────▼──────────┐                │
│                         │   Sample Generator   │                │
│                         │   + AI Service       │                │
│                         └──────────┬──────────┘                │
└────────────────────────────────────┼───────────────────────────┘
                                     │
         ┌───────────┬───────────┬───┴───┬───────────┬───────────┐
         ▼           ▼           ▼       ▼           ▼           ▼
    ┌─────────┐ ┌─────────┐ ┌─────────┐ ┌─────────┐ ┌─────────────┐
    │  .NET   │ │ Python  │ │  Java   │ │   Go    │ │ TypeScript  │
    │ Roslyn  │ │   ast   │ │JavaParser│ │go/parser│ │  ts-morph   │
    └─────────┘ └─────────┘ └─────────┘ └─────────┘ └─────────────┘
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
dotnet new classlib -n ApiExtractor.NewLang -o ApiExtractor.NewLang
dotnet sln add ApiExtractor.NewLang
```

Add reference:
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

Create `ApiExtractor.Tests/NewLangApiExtractorTests.cs`:

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

Add fixtures in `ApiExtractor.Tests/TestFixtures/NewLang/`.

### 7. Register in Main Tool

Update `Sdk.Tools.Chat/Services/Languages/LanguageDetector.cs` to detect the new language.

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

## Testing

### Run All Tests

```bash
dotnet test
```

### Run Specific Tests

```bash
# By project
dotnet test ApiExtractor.Tests

# By filter
dotnet test --filter "FullyQualifiedName~DotNetApiExtractor"

# By category
dotnet test --filter "Category=Integration"
```

### Test Fixtures

Located in `ApiExtractor.Tests/TestFixtures/<Language>/`:
- Include classes, interfaces, enums
- Cover generics, async, edge cases
- Keep minimal but representative

---

## Building

```bash
# Build all
dotnet build

# Run single extractor
dotnet run --project ApiExtractor.DotNet -- /path --json --pretty

# Pack as tool
dotnet pack Sdk.Tools.Chat -o ./artifacts

# Publish self-contained
dotnet publish Sdk.Tools.Chat -c Release -r linux-x64 --self-contained
```

---

## Questions?

Open an issue or check existing discussions.
