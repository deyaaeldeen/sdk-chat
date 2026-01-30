# API Extractor Benchmarks

Real-world benchmark results from high-profile open-source SDKs.

## Results Summary

### OpenAI SDKs

| SDK | Language | Source Size | Extracted Size | Reduction |
|-----|----------|-------------|----------------|-----------|
| [openai-dotnet](https://github.com/openai/openai-dotnet) | C# | 10.0 MB | 477 KB | **95.2%** |
| [openai-python](https://github.com/openai/openai-python) | Python | 3.7 MB | 339 KB | **90.8%** |
| [openai-node](https://github.com/openai/openai-node) | TypeScript | 1.6 MB | 651 KB | **58.9%** |
| [openai-java](https://github.com/openai/openai-java) | Java | 108 KB | 16 KB | **85.0%** |
| [openai-go](https://github.com/openai/openai-go) | Go | 2.6 MB | 1.6 MB | **38.1%** |

### Stripe SDKs

| SDK | Language | Source Size | Extracted Size | Reduction |
|-----|----------|-------------|----------------|-----------|
| [stripe-dotnet](https://github.com/stripe/stripe-dotnet) | C# | 10.7 MB | 4.2 MB | **60.5%** |
| [stripe-node](https://github.com/stripe/stripe-node) | TypeScript | 251 KB | 28 KB | **88.9%** |
| [stripe-java](https://github.com/stripe/stripe-java) | Java | 26.2 MB | 1.6 MB | **94.0%** |
| [stripe-go](https://github.com/stripe/stripe-go) | Go | 8.5 MB | 6.8 MB | **20.1%** |

### AWS SDKs

| SDK | Language | Source Size | Extracted Size | Reduction |
|-----|----------|-------------|----------------|-----------|
| [aws-sdk-net (S3)](https://github.com/aws/aws-sdk-net) | C# | 12.4 MB | 799 KB | **93.6%** |
| [boto3](https://github.com/boto/boto3) | Python | 308 KB | 31 KB | **89.9%** |
| [aws-sdk-go-v2 (S3)](https://github.com/aws/aws-sdk-go-v2) | Go | 3.7 MB | 299 KB | **91.9%** |

**Average reduction: 75.4%**

## Token Estimates

Using standard approximation of ~4 chars per token:

| SDK | Source Tokens | Extracted Tokens | Reduction |
|-----|---------------|------------------|-----------|
| openai-dotnet | ~2.5M | ~119K | **95.2%** |
| stripe-java | ~6.5M | ~390K | **94.0%** |
| aws-sdk-net (S3) | ~3.1M | ~200K | **93.6%** |
| aws-sdk-go-v2 (S3) | ~924K | ~75K | **91.9%** |
| openai-python | ~924K | ~85K | **90.8%** |
| boto3 | ~77K | ~8K | **89.9%** |

## Context Window Impact

Using the stripe-java SDK (26.2 MB source, 1.6 MB extracted = ~390K tokens):

| Model | Context Limit | stripe-java Full | stripe-java Extracted |
|-------|---------------|------------------|----------------------|
| GPT-5.2 | 400K | ❌ Exceeds by 16x | ✅ Fits with room |
| GPT-4.1 | 1M | ❌ Exceeds by 6.5x | ✅ Fits 2.5x over |
| Claude Sonnet 4.5 | 200K (1M beta) | ❌ Exceeds by 33x | ✅ Fits with room |
| Claude Opus 4.5 | 200K | ❌ Exceeds by 33x | ✅ Fits with room |

*Model context limits sourced from OpenAI and Anthropic documentation, January 2026*

## Methodology

- **Source Size**: All source files (`*.cs`, `*.py`, `*.ts`, `*.java`, `*.go`) concatenated
- **Extracted Size**: JSON output from API extractor
- **Reduction**: `(1 - extracted/source) × 100%`
- **Time**: Cold start including .NET runtime and language tooling startup

## What's Extracted

The API surface includes:
- Public classes, interfaces, structs, enums
- Public methods with signatures and return types
- Public properties and fields
- Constructor signatures
- First line of documentation comments
- Type inheritance and interface implementation

## What's Excluded

- Implementation code (method bodies)
- Private/internal members
- Comments beyond first line
- Test files
- Build artifacts
- Generated code (when detectable)

## Reproduction

```bash
# Clone an SDK
git clone --depth 1 https://github.com/openai/openai-dotnet.git

# Measure source
find openai-dotnet/src -name "*.cs" | xargs cat | wc -c

# Extract API
dotnet run --project ApiExtractor.DotNet -- openai-dotnet/src --json | wc -c
```

## Performance Notes

- **C# (Roslyn)**: Slowest cold start (~7s) due to Roslyn compilation, but always available
- **Python (ast)**: Fastest (~2s) due to lightweight AST parsing
- **Go (go/parser)**: Fast (~2.4s) after first-run compilation caching
- **TypeScript (ts-morph)**: Moderate (~6s) due to Node.js + ts-morph startup
- **Java (JBang)**: Moderate (~4-7s) due to JVM startup + JavaParser

For batch operations, consider the worker process pattern documented in [EXTRACTOR_OPTIMIZATION_PLAN.md](EXTRACTOR_OPTIMIZATION_PLAN.md).

---

*Benchmarks run on Linux x64, .NET 10.0, January 2026*
