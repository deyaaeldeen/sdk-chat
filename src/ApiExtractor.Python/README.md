# ApiExtractor.Python

Python API extractor using the `ast` module.

## Overview

Extracts public API surface from Python packages by spawning a Python subprocess that uses the built-in `ast` module.

## Features

- Parses `.py` files
- Extracts classes, functions, methods
- Captures type hints (PEP 484)
- Handles docstrings
- Respects `_private` naming conventions

## Architecture

| File | Description |
|------|-------------|
| `PythonApiExtractor.cs` | Orchestrates Python subprocess |
| `extract_api.py` | Python script that does the actual parsing |
| `PythonFormatter.cs` | Output formatting |
| `PythonUsageAnalyzer.cs` | Analyzes imports in samples |

## How It Works

1. C# spawns `python3 extract_api.py <path>`
2. Python script walks the AST
3. JSON output piped back to C#
4. C# formats as API surface

## Requirements

- `python3` in PATH
- No external Python packages required (uses stdlib `ast`)

## Development

```bash
# Test extractor
dotnet test tests/ApiExtractor.Tests --filter "FullyQualifiedName~Python"

# Test Python script directly
python3 src/ApiExtractor.Python/extract_api.py /path/to/python/package
```
