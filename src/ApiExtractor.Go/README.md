# ApiExtractor.Go

Go API extractor using go/parser.

## Overview

Extracts public API surface from Go modules by spawning a Go subprocess that uses the standard library's `go/parser` and `go/ast` packages.

## Features

- Parses `.go` files
- Extracts exported types, functions, methods
- Captures struct fields and interfaces
- Handles generics (Go 1.18+)
- Respects Go visibility (exported = capitalized)

## Architecture

| File | Description |
|------|-------------|
| `GoApiExtractor.cs` | Orchestrates Go subprocess |
| `extract_api.go` | Go script using go/parser |
| `GoFormatter.cs` | Output formatting |
| `GoUsageAnalyzer.cs` | Analyzes imports in samples |

## How It Works

1. C# spawns `go run extract_api.go <path>`
2. Go script walks the AST
3. JSON output piped back to C#
4. C# formats as API surface

## Requirements

- `go` in PATH (1.18+)
- No external Go packages required (uses stdlib)

## Development

```bash
# Test extractor
dotnet test tests/ApiExtractor.Tests --filter "FullyQualifiedName~Go"

# Test Go script directly
go run src/ApiExtractor.Go/extract_api.go /path/to/go/module
```
