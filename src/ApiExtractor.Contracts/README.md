# ApiExtractor.Contracts

Shared interfaces and types for API extractors.

## Overview

Defines the contracts that all language-specific API extractors must implement.

## Key Interfaces

### `IApiExtractor`

```csharp
public interface IApiExtractor
{
    string Language { get; }
    Task<ApiSurface> ExtractAsync(string sourcePath, CancellationToken ct);
}
```

### `IUsageAnalyzer`

```csharp
public interface IUsageAnalyzer
{
    Task<IReadOnlyList<string>> GetImportedTypesAsync(string samplePath, CancellationToken ct);
}
```

## Key Types

| Type | Description |
|------|-------------|
| `ApiSurface` | Extracted API surface model |
| `ApiType` | Type definition (class, interface, enum) |
| `ApiMethod` | Method signature |
| `ApiProperty` | Property definition |
| `CliOptions` | Shared CLI parsing options |
| `ToolPathResolver` | Finds external tools (python3, node, etc.) |

## Usage

Implement `IApiExtractor` to add support for a new language:

```csharp
public class MyLanguageExtractor : IApiExtractor
{
    public string Language => "mylang";
    
    public async Task<ApiSurface> ExtractAsync(string sourcePath, CancellationToken ct)
    {
        // Parse source files
        // Extract public API surface
        // Return structured model
    }
}
```

## Development

```bash
dotnet test tests/ApiExtractor.Tests
```
