# API Extractor Optimization Plan

This document outlines four optimization strategies to reduce cold-start latency for the API extractors. Each section is designed to be executed independently by an AI agent.

---

## Current Performance Baseline

| Extractor | Cold Start | Output Size | Primary Bottleneck |
|-----------|------------|-------------|-------------------|
| Python | 32ms | 85KB | None (fast enough) |
| Go | 139ms | 23KB | `go run` compilation |
| TypeScript | 1.5s | 61KB | ts-morph TypeScript compiler load |
| Java | 1.5s | 59KB | JVM startup + JBang |

---

## Optimization 1: TypeScript Pre-Bundling with esbuild

### Goal
Reduce TypeScript extractor cold start from ~1.5s to ~500ms by bundling all dependencies into a single file.

### Why It Works
- ts-morph has 12 npm dependencies
- Node.js module resolution adds ~200-400ms per invocation
- Single-file bundle eliminates require() overhead

### Implementation Steps

#### Step 1: Add esbuild as a dev dependency
```bash
cd tools/sdk-chat/ApiExtractor.TypeScript
npm install --save-dev esbuild
```

#### Step 2: Create build script in package.json
Update `tools/sdk-chat/ApiExtractor.TypeScript/package.json`:
```json
{
  "scripts": {
    "build": "tsc -p src/tsconfig.json",
    "bundle": "esbuild dist/extract_api.js --bundle --platform=node --target=node20 --outfile=dist/extract_api.bundle.js --minify",
    "build:prod": "npm run build && npm run bundle"
  }
}
```

#### Step 3: Update TypeScriptApiExtractor.cs to prefer bundle
In `tools/sdk-chat/ApiExtractor.TypeScript/TypeScriptApiExtractor.cs`, update `ExtractAsync`:
```csharp
// Priority: bundle > compiled > mjs fallback
var scriptPath = Path.Combine(scriptDir, "dist", "extract_api.bundle.js");
if (!File.Exists(scriptPath))
{
    scriptPath = Path.Combine(scriptDir, "dist", "extract_api.js");
}
if (!File.Exists(scriptPath))
{
    scriptPath = Path.Combine(scriptDir, "extract_api.mjs");
}
```

#### Step 4: Update csproj to include bundle
In `tools/sdk-chat/ApiExtractor.TypeScript/ApiExtractor.TypeScript.csproj`:
```xml
<ItemGroup>
  <None Include="dist/extract_api.bundle.js" Condition="Exists('dist/extract_api.bundle.js')">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    <Link>dist/extract_api.bundle.js</Link>
  </None>
</ItemGroup>
```

#### Step 5: Update CI/CD to run bundle step
Add to build pipeline:
```bash
cd tools/sdk-chat/ApiExtractor.TypeScript
npm ci
npm run build:prod
```

### Verification
```bash
# Before
time node dist/extract_api.js /path/to/sdk --json | wc -c

# After  
time node dist/extract_api.bundle.js /path/to/sdk --json | wc -c
```

Expected improvement: ~30-40% reduction in startup time.

### Rollback
If issues occur, the extractor automatically falls back to unbundled version.

---

## Optimization 2: Go Pre-Compilation

### Goal
Eliminate `go run` compilation overhead by pre-compiling to a binary.

### Why It Works
- `go run` compiles on every invocation (~100-200ms)
- Pre-compiled binary starts in ~10ms
- Go produces static binaries with no runtime dependencies

### Implementation Steps

#### Step 1: Create build script
Create `tools/sdk-chat/ApiExtractor.Go/build.sh`:
```bash
#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

# Build for current platform
go build -o extract_api -ldflags="-s -w" extract_api.go

# Optionally build for all platforms
if [ "$1" == "--all" ]; then
    GOOS=linux GOARCH=amd64 go build -o extract_api_linux_amd64 -ldflags="-s -w" extract_api.go
    GOOS=darwin GOARCH=amd64 go build -o extract_api_darwin_amd64 -ldflags="-s -w" extract_api.go
    GOOS=darwin GOARCH=arm64 go build -o extract_api_darwin_arm64 -ldflags="-s -w" extract_api.go
    GOOS=windows GOARCH=amd64 go build -o extract_api_windows_amd64.exe -ldflags="-s -w" extract_api.go
fi

echo "Build complete"
```

#### Step 2: Update GoApiExtractor.cs
Modify `tools/sdk-chat/ApiExtractor.Go/GoApiExtractor.cs`:

```csharp
public async Task<ApiIndex?> ExtractAsync(string rootPath, CancellationToken ct = default)
{
    var (executable, args) = GetExtractorCommand(rootPath, "--json");
    
    var psi = new ProcessStartInfo
    {
        FileName = executable,
        Arguments = args,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    // ... rest of method
}

private (string executable, string args) GetExtractorCommand(string rootPath, string outputFlag)
{
    var assemblyDir = Path.GetDirectoryName(typeof(GoApiExtractor).Assembly.Location) ?? ".";
    
    // Try pre-compiled binary first
    var binaryName = OperatingSystem.IsWindows() ? "extract_api.exe" : "extract_api";
    var binaryPath = Path.Combine(assemblyDir, binaryName);
    
    if (File.Exists(binaryPath))
    {
        return (binaryPath, $"{outputFlag} \"{rootPath}\"");
    }
    
    // Fall back to go run
    var goPath = _goPath ?? FindGoExecutable() 
        ?? throw new FileNotFoundException("Go executable not found");
    var scriptPath = GetScriptPath();
    
    return (goPath, $"run \"{scriptPath}\" {outputFlag} \"{rootPath}\"");
}
```

#### Step 3: Update csproj to include binary
```xml
<ItemGroup>
  <None Include="extract_api" Condition="Exists('extract_api') AND '$([MSBuild]::IsOSPlatform(Linux))' == 'true' OR '$([MSBuild]::IsOSPlatform(OSX))' == 'true'">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
  <None Include="extract_api.exe" Condition="Exists('extract_api.exe') AND '$([MSBuild]::IsOSPlatform(Windows))' == 'true'">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
</ItemGroup>
```

#### Step 4: Add to CI/CD
```bash
cd tools/sdk-chat/ApiExtractor.Go
chmod +x build.sh
./build.sh --all
```

### Verification
```bash
# Before
time go run extract_api.go --json /path/to/sdk | wc -c

# After
time ./extract_api --json /path/to/sdk | wc -c
```

Expected improvement: ~90% reduction (139ms → ~15ms).

### Rollback
Falls back to `go run` if binary not found.

---

## Optimization 3: Java Native Image (GraalVM)

### Goal
Eliminate JVM startup overhead by compiling to native binary using GraalVM.

### Why It Works
- JVM startup adds ~800ms-1.2s
- GraalVM native-image produces ahead-of-time compiled binaries
- Native binary starts in ~10-50ms

### Complexity Warning
This is the most complex optimization. GraalVM native-image requires:
- Reflection configuration for JavaParser and Gson
- Build infrastructure for native compilation
- Platform-specific binaries

### Implementation Steps

#### Step 1: Create reflection configuration
Create `tools/sdk-chat/ApiExtractor.Java/native-image/reflect-config.json`:
```json
[
  {
    "name": "com.github.javaparser.ast.CompilationUnit",
    "allDeclaredMethods": true,
    "allDeclaredFields": true
  },
  {
    "name": "com.google.gson.internal.bind.ReflectiveTypeAdapterFactory",
    "allDeclaredMethods": true
  }
]
```

#### Step 2: Create native-image build script
Create `tools/sdk-chat/ApiExtractor.Java/build-native.sh`:
```bash
#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

# Ensure GraalVM is available
if ! command -v native-image &> /dev/null; then
    echo "GraalVM native-image not found. Install with:"
    echo "  sdk install java 21.0.2-graalce"
    echo "  gu install native-image"
    exit 1
fi

# Download dependencies via JBang
jbang export portable --force -O=lib ExtractApi.java

# Compile with native-image
native-image \
    --no-fallback \
    --enable-url-protocols=https \
    -H:ReflectionConfigurationFiles=native-image/reflect-config.json \
    -H:+ReportExceptionStackTraces \
    -cp "lib/*" \
    ExtractApi \
    -o extract_api

echo "Native build complete"
```

#### Step 3: Update JavaApiExtractor.cs
```csharp
public async Task<ApiIndex?> ExtractAsync(string rootPath, CancellationToken ct = default)
{
    var (executable, args) = GetExtractorCommand(rootPath, "--json");
    
    var psi = new ProcessStartInfo
    {
        FileName = executable,
        Arguments = args,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    
    // ... rest unchanged
}

private (string executable, string args) GetExtractorCommand(string rootPath, string outputFlag)
{
    var assemblyDir = Path.GetDirectoryName(typeof(JavaApiExtractor).Assembly.Location) ?? ".";
    
    // Try native binary first
    var binaryName = OperatingSystem.IsWindows() ? "extract_api.exe" : "extract_api";
    var nativePath = Path.Combine(assemblyDir, binaryName);
    
    if (File.Exists(nativePath))
    {
        return (nativePath, $"\"{rootPath}\" {outputFlag}");
    }
    
    // Fall back to JBang
    var scriptPath = GetScriptPath();
    return ("jbang", $"\"{scriptPath}\" \"{rootPath}\" {outputFlag}");
}
```

#### Step 4: Generate reflection config automatically (alternative)
Run with tracing agent to generate config:
```bash
java -agentlib:native-image-agent=config-output-dir=native-image \
    -jar extract_api.jar /path/to/sample/sdk --json > /dev/null
```

### Verification
```bash
# Before
time jbang ExtractApi.java /path/to/sdk --json | wc -c

# After
time ./extract_api /path/to/sdk --json | wc -c
```

Expected improvement: ~95% reduction (1.5s → ~50ms).

### Rollback
Falls back to JBang if native binary not found.

### Risks
- GraalVM native-image has limited reflection support
- JavaParser uses reflection heavily
- May require ongoing maintenance of reflection config

---

## Optimization 4: Worker Process Pattern

### Goal
For batch processing scenarios, keep extractors warm to amortize startup cost.

### Why It Works
- Single startup cost shared across multiple extractions
- JSON-over-stdin/stdout communication
- Process stays alive between requests

### When To Use
- Processing multiple packages in sequence
- CI/CD pipeline extracting many SDKs
- IDE integration with frequent re-extraction

### Implementation Steps

#### Step 1: Define worker protocol
The worker accepts JSON-RPC style messages over stdin:
```json
{"id": 1, "method": "extract", "params": {"path": "/path/to/sdk", "format": "json"}}
{"id": 2, "method": "extract", "params": {"path": "/other/sdk", "format": "stub"}}
{"id": 3, "method": "shutdown"}
```

Responses:
```json
{"id": 1, "result": {"package": "...", "modules": [...]}}
{"id": 2, "result": "// stub output..."}
{"id": 3, "result": "ok"}
```

#### Step 2: Create TypeScript worker mode
Update `tools/sdk-chat/ApiExtractor.TypeScript/src/extract_api.ts`:
```typescript
// Add at end of file, before main()
async function workerMode(): Promise<void> {
    const readline = await import('readline');
    const rl = readline.createInterface({ input: process.stdin });

    for await (const line of rl) {
        try {
            const request = JSON.parse(line);
            const { id, method, params } = request;

            if (method === 'shutdown') {
                console.log(JSON.stringify({ id, result: 'ok' }));
                process.exit(0);
            }

            if (method === 'extract') {
                const api = extractPackage(params.path);
                const result = params.format === 'stub' 
                    ? formatStubs(api)
                    : api;
                console.log(JSON.stringify({ id, result }));
            }
        } catch (error) {
            const message = error instanceof Error ? error.message : String(error);
            console.log(JSON.stringify({ id: null, error: message }));
        }
    }
}

function main(): void {
    const args = process.argv.slice(2);
    
    if (args.includes('--worker')) {
        workerMode();
        return;
    }
    
    // ... existing CLI logic
}
```

#### Step 3: Create C# worker client
Create `tools/sdk-chat/ApiExtractor.Contracts/ExtractorWorker.cs`:
```csharp
using System.Diagnostics;
using System.Text.Json;

namespace ApiExtractor.Contracts;

/// <summary>
/// Manages a persistent extractor worker process for batch operations.
/// </summary>
public sealed class ExtractorWorker : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _writer;
    private readonly StreamReader _reader;
    private int _requestId;
    
    private ExtractorWorker(Process process)
    {
        _process = process;
        _writer = process.StandardInput;
        _reader = process.StandardOutput;
    }
    
    public static async Task<ExtractorWorker> StartAsync(
        string executable, 
        string arguments,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments + " --worker",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        
        var process = Process.Start(psi) 
            ?? throw new InvalidOperationException("Failed to start worker");
        
        return new ExtractorWorker(process);
    }
    
    public async Task<T> ExtractAsync<T>(
        string path, 
        string format = "json",
        CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _requestId);
        var request = new { id, method = "extract", @params = new { path, format } };
        
        await _writer.WriteLineAsync(JsonSerializer.Serialize(request));
        await _writer.FlushAsync();
        
        var responseLine = await _reader.ReadLineAsync(ct) 
            ?? throw new InvalidOperationException("Worker closed unexpectedly");
        
        var response = JsonSerializer.Deserialize<WorkerResponse<T>>(responseLine);
        
        if (response?.Error != null)
            throw new InvalidOperationException(response.Error);
        
        return response!.Result!;
    }
    
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _writer.WriteLineAsync(
                JsonSerializer.Serialize(new { id = 0, method = "shutdown" }));
            await _writer.FlushAsync();
            
            if (!_process.WaitForExit(1000))
                _process.Kill();
        }
        catch { }
        
        _process.Dispose();
    }
    
    private record WorkerResponse<T>(int Id, T? Result, string? Error);
}
```

#### Step 4: Usage example
```csharp
// Batch processing multiple SDKs
await using var worker = await ExtractorWorker.StartAsync(
    "node", 
    "dist/extract_api.js");

var results = new List<ApiIndex>();
foreach (var sdkPath in sdkPaths)
{
    var api = await worker.ExtractAsync<ApiIndex>(sdkPath);
    results.Add(api);
}
// Worker automatically shuts down on dispose
```

### Verification
```bash
# Single invocation baseline
time for i in {1..10}; do node dist/extract_api.js /path/to/sdk --json > /dev/null; done

# Worker mode
time (echo '{"id":1,"method":"extract","params":{"path":"/path/to/sdk","format":"json"}}
{"id":2,"method":"extract","params":{"path":"/path/to/sdk","format":"json"}}
...repeat 10x...
{"id":11,"method":"shutdown"}' | node dist/extract_api.js --worker > /dev/null)
```

Expected improvement: ~90% for batch operations (10 invocations: 15s → 2s).

### Risks
- Process management complexity
- Memory accumulation in long-running workers
- Need graceful handling of worker crashes

---

## Implementation Priority

| Optimization | Effort | Impact | Recommended |
|--------------|--------|--------|-------------|
| TypeScript Bundle | Low | Medium | ✅ Yes |
| Go Binary | Low | High | ✅ Yes |
| Java Native | High | High | ⚠️ If Java is critical path |
| Worker Pattern | Medium | High | ⚠️ If batch processing needed |

### Recommended Execution Order
1. **Go Binary** - Easiest win, biggest percentage improvement
2. **TypeScript Bundle** - Low effort, good improvement
3. **Worker Pattern** - If batch processing is a use case
4. **Java Native** - Only if Java extraction is frequently used

---

## Testing Requirements

After implementing any optimization:

1. Run existing test suite:
   ```bash
   cd tools/sdk-chat
   dotnet test ApiExtractor.Tests
   ```

2. Verify output parity:
   ```bash
   # Compare optimized vs original output
   diff <(./optimized --json /path) <(./original --json /path)
   ```

3. Benchmark improvement:
   ```bash
   hyperfine --warmup 3 './optimized --json /path' './original --json /path'
   ```

---

## Rollback Strategy

All optimizations maintain backward compatibility:
- TypeScript: Falls back to unbundled → mjs
- Go: Falls back to `go run`
- Java: Falls back to JBang
- Worker: Not a replacement, additive feature

No changes to the `IApiExtractor<T>` interface required.
