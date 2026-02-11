# ApiExtractor.TypeScript

TypeScript API extractor using ts-morph.

## Overview

Extracts public API surface from TypeScript packages by spawning a Node.js subprocess that uses ts-morph.

## Features

- Parses `.ts` and `.tsx` files
- Extracts classes, interfaces, functions, types
- Captures JSDoc comments
- Handles generics and union types
- Respects `export` visibility

## Architecture

| File | Description |
|------|-------------|
| `TypeScriptApiExtractor.cs` | Orchestrates Node subprocess |
| `src/extract_api.ts` | TypeScript source (compiled to `dist/extract_api.js`) |
| `TypeScriptFormatter.cs` | Output formatting |
| `TypeScriptUsageAnalyzer.cs` | Analyzes imports in samples |

## How It Works

1. C# spawns `node dist/extract_api.js <path>`
2. Script uses ts-morph to parse TypeScript
3. JSON output piped back to C#
4. C# formats as API surface

## Requirements

- `node` in PATH (v18+)
- ts-morph installed in the extractor directory

## Setup

```bash
cd src/ApiExtractor.TypeScript
npm install
```

## Development

```bash
# Test extractor
dotnet test tests/ApiExtractor.Tests --filter "FullyQualifiedName~TypeScript"

# Test Node script directly
node src/ApiExtractor.TypeScript/dist/extract_api.js /path/to/ts/package
```
