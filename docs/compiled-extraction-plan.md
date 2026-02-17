# Public API Graph Engine — Design Plan

## Overview

Enhance existing Public API Graph Engines with compiled-artifact analysis as a higher-fidelity input path. Each engine gains an internal "try compiled artifacts first, fall back to source" strategy — **no new engine classes, no CompositeEngine, no parallel hierarchy**. If the SDK has been built and artifacts exist, the engine uses them for 100% type accuracy. If artifacts are absent, the engine falls back to its current source-parsing behavior transparently. TypeScript gains multi-target export condition support (node/browser/etc.).

### Goals

- **100% type accuracy when artifacts exist**: every type in every public API signature fully resolves — no `TypeKind.Error`, no missing imports, no string-based guessing.
- **Graceful degradation**: if compiled artifacts are absent, the engine falls back to source-parsing (current behavior) automatically. No user action required.
- **No new abstractions**: no `CompositeEngine`, no parallel engine classes. Each existing engine gets smarter internally. The `IPublicApiGraphEngine<TIndex>` interface is unchanged.
- **Zero heuristics for build output**: when artifacts are present, their location is resolved by querying the build system itself.

### Non-Goals

- Building SDKs, installing dependencies, or downloading packages.
- Discovering project files — that's the caller's responsibility.
- Creating separate compiled-mode engine classes.

### Preconditions for compiled mode (optional — engine works without them)

| Language | What enables compiled-artifact path |
|----------|-------------------------------------------------------|
| **C#** | `dotnet build <csproj>` (produces DLL + XML docs) |
| **TypeScript** | `npm install && npm run build` (produces `.d.ts` files) |
| **Python** | `pip install -e .` (makes package importable via `python`) |
| **Java (Maven)** | `mvn compile -f <pom.xml>` (produces `.class` files) |
| **Java (Gradle)** | `gradle compileJava -b <build.gradle>` (produces `.class` files) |
| **Go** | No change — engine already type-checks via `packages.Load()` |

If none of these have been run, each engine falls back to its existing source-parsing path.

---

## Architecture

### CLI API

The shared `CliOptions` in `PublicApiGraphEngine.Contracts` gains language-specific optional parameters for artifact paths. The caller provides the concrete build-system file path — no heuristics, no guessing which `.csproj` or `pom.xml` to use.

**Current:**
```
<engine> <path> [--json] [--stub] [--pretty] [-o <file>]
```

**Proposed:**
```
<engine> <path> [--json] [--stub] [--pretty] [-o <file>]
         [--csproj <file>]           # C# only: .csproj path → resolves DLL
         [--tsconfig <file>]         # TypeScript only: tsconfig.json path → resolves .d.ts dir
         [--package-json <file>]     # TypeScript only: package.json path → resolves export conditions
         [--pom <file>]             # Java only: pom.xml path → resolves classes dir
         [--gradle <file>]          # Java only: build.gradle path → resolves classes dir
         [--import-name <name>]     # Python only: importable package name → runtime inspection
```

**Behavior:**
- If the artifact flag is provided, the engine resolves build output from that path and uses artifact-based analysis. If the resolved artifacts don't exist (e.g., DLL not built), the engine falls back to source parsing from `<path>` and emits a warning diagnostic.
- If no artifact flag is provided, the engine uses source parsing from `<path>` — current behavior exactly.
- Go engine: no artifact flag needed (already at artifact quality via `packages.Load()`).

`<path>` remains the SDK source root in all cases — it's still needed for doc comment extraction, package name detection, and source-mode fallback.

### Interface — No Changes

```
IPublicApiGraphEngine<TIndex>                    (existing — unchanged)
├── GraphAsync(rootPath, ct) → EngineResult<TIndex>
├── Language: string
├── IsAvailable(): bool
├── ToJson(index) → string
└── ToStubs(index) → string
```

Artifact paths are passed to engines via constructor parameters or a shared options object, not through `GraphAsync`. The `rootPath` parameter continues to be the SDK source root.

### Registry — Minor Signature Change

The `AnalyzerRegistry` factory lambdas gain an optional `ArtifactOptions?` parameter so artifact paths can be threaded into engine constructors:

```csharp
AnalyzerRegistry = {
    [DotNet]      = (opts) => (new CSharpPublicApiGraphEngine(opts?.CsprojPath), new CSharpUsageAnalyzer()),
    [Python]      = (opts) => (new PythonPublicApiGraphEngine(opts?.ImportName), new PythonUsageAnalyzer()),
    [Go]          = (opts) => (new GoPublicApiGraphEngine(), new GoUsageAnalyzer()),
    [TypeScript]  = (opts) => (new TypeScriptPublicApiGraphEngine(opts?.TsconfigPath, opts?.PackageJsonPath), new TypeScriptUsageAnalyzer()),
    [JavaScript]  = (opts) => (new TypeScriptPublicApiGraphEngine(opts?.TsconfigPath, opts?.PackageJsonPath), new TypeScriptUsageAnalyzer()),
    [Java]        = (opts) => (new JavaPublicApiGraphEngine(opts?.PomPath, opts?.GradlePath), new JavaUsageAnalyzer()),
};

// ArtifactOptions — all fields nullable, all optional
public sealed record ArtifactOptions
{
    public string? CsprojPath { get; init; }
    public string? TsconfigPath { get; init; }
    public string? PackageJsonPath { get; init; }
    public string? PomPath { get; init; }
    public string? GradlePath { get; init; }
    public string? ImportName { get; init; }
}
```

When `opts` is `null` (no artifact flags provided), engines behave exactly as today.

### Per-Engine Internal Structure

Each engine's `GraphAsync` becomes:

```
GraphAsync(rootPath, ct):
  if no artifact path was provided (via constructor/options):
    return SourcePath(rootPath, ct)    // current behavior, unchanged

  artifacts = ResolveArtifacts(artifactPath)  // query build tool for output location
  if artifacts exist on disk:
    log info: "Using compiled artifacts from {artifacts.Path}"
    return ArtifactPath(rootPath, artifacts, ct)
  else:
    log warning: "Artifacts not found at resolved path, falling back to source"
    return SourcePath(rootPath, ct)    // current behavior, unchanged
```

The `SourcePath` is the existing `GraphAsync` logic extracted into a private method. The `ArtifactPath` is the new code path. Both produce the same `ApiIndex` output type.

### Diagnostics

Engines emit structured `ApiDiagnostic` entries indicating which path was taken and any issues encountered:

```
// Artifact path activated successfully
{ Category: "EngineInput", Severity: Info,
  Message: "Using compiled artifacts: DLL at /path/to/sdk.dll" }

// Artifact path failed, fell back to source
{ Category: "EngineInput", Severity: Warning,
  Message: "DLL not found at /path/to/sdk.dll. Falling back to source analysis. Run: dotnet build /path/to/sdk.csproj" }

// Build tool not available
{ Category: "EngineInput", Severity: Warning,
  Message: "dotnet CLI not found on PATH. Falling back to source analysis." }

// No artifact flag provided (implicit source mode)
// No diagnostic emitted — this is the default, no noise.
```

These diagnostics appear in `EngineResult.Diagnostics` alongside any existing engine diagnostics (e.g., unresolved type warnings).

---

## Per-Language Engine Enhancements

### C#/.NET — `CSharpPublicApiGraphEngine`

The existing engine uses Roslyn to parse `.cs` source files into a `CSharpCompilation` and walk symbols. The artifact path replaces the source-file input with a DLL metadata reference — **the same Roslyn symbol-walking code runs on both paths**.

#### Artifact Resolution (from `--csproj` flag)

```
ResolveArtifacts(csprojPath):  // csprojPath provided by caller via --csproj
  0. If `dotnet` is not on PATH → return null, emit warning:
     "dotnet CLI not found. Falling back to source."

  1. Shell out: dotnet msbuild <csprojPath> -getProperty:TargetPath -target:GetTargetPath
     → Returns the ABSOLUTE path to the compiled .dll.
     This evaluates ALL MSBuild property chains:
       OutputPath, OutDir, BaseOutputPath, AssemblyName, TargetFramework,
       Configuration, Directory.Build.props imports, custom targets.

     For multi-target projects (<TargetFrameworks>):
       Shell out: dotnet msbuild <csprojPath> -getProperty:TargetFrameworks
       → Returns semicolon-separated list (e.g., "net8.0;netstandard2.0").
       For each TFM:
         dotnet msbuild <csprojPath> -getProperty:TargetPath -property:TargetFramework=<tfm> -target:GetTargetPath
       Pick the highest-versioned TFM.

  2. If msbuild invocation fails (non-zero exit, timeout) → return null, emit warning:
     "Failed to query MSBuild for target path. Falling back to source."

  3. If .dll does not exist at the resolved path → return null (fall back to source).
     Emit warning: "DLL not found at {path}. Falling back to source. Run: dotnet build {csprojPath}"

  4. XML doc sidecar: same directory, same filename with .xml extension.
     If absent → add warning diagnostic (doc comments will be missing), continue.

  Timeout: 30s for each msbuild invocation.
```

#### Artifact Analysis Path

```
ArtifactPath(rootPath, artifacts, ct):
  1. Load the SDK's DLL + all NuGet dependency DLLs into a CSharpCompilation:
     - MetadataReference.CreateFromFile(dllPath) — the SDK's own assembly
     - Runtime assembly refs (System.Runtime, etc.) — from AppContext.BaseDirectory
     - NuGet dependency DLLs — resolved from the .csproj's obj/project.assets.json
       (existing logic from CSharpPublicApiGraphEngine.Dependencies.cs — reused)

  2. Walk compilation.GlobalNamespace recursively.
     *** This is the SAME symbol-walking code as the source path ***
     For each INamedTypeSymbol where DeclaredAccessibility == Public:
       - GetMembers() → constructors, methods, properties, events, indexers, operators
       - Fully resolved generic type parameters, nullable annotations
       - async detection from return type (Task/ValueTask/IAsyncEnumerable)

  3. Doc comments: compute XML doc ID for each member (M:Namespace.Type.Method(ParamType))
     and look up in the parsed .xml sidecar file.
     If XML sidecar is absent, fall back to parsing doc comments from source `.cs` files
     in `rootPath` (match by fully-qualified member name). This hybrid approach gives
     doc comments even when the build didn't produce an XML file.

  4. Dependencies: every ITypeSymbol in a public signature whose
     ContainingAssembly.Name ≠ SDK's own assembly → external dependency.
     Group by assembly name. 100% accurate — no string parsing.
```

**Key insight**: the existing engine already creates a `CSharpCompilation` from source. The artifact path just changes *how* the compilation is populated (metadata refs instead of syntax trees). The symbol-walking, formatting, and dependency-detection code is shared.

**Correctness advantage**: the DLL path correctly excludes `internal` members even when `[InternalsVisibleTo]` is used. The source path sees all accessibility levels and filters syntactically — the DLL path gets the actual public API surface that consumers see.

#### Source Path (unchanged)

Existing `GraphAsync` logic: enumerate `.cs` files, parse syntax trees, create compilation, walk symbols.

---

### TypeScript — `TypeScriptPublicApiGraphEngine`

The existing engine uses ts-morph on `.ts` source files. The artifact path points ts-morph at `.d.ts` files instead — **ts-morph works identically on both**. This engine also gains export-condition enumeration.

#### Artifact Resolution (from `--tsconfig` and `--package-json` flags)

```
ResolveArtifacts(tsconfigPath, packageJsonPath):  // provided by caller via --tsconfig / --package-json
  0. If `tsc` is not on PATH → return null, emit warning:
     "tsc not found. Falling back to source."

  1. Shell out: tsc -p <tsconfigPath> --showConfig
     → Returns fully merged config as JSON (all "extends" chains resolved).
     Requires node_modules to be populated (extends from packages must resolve).
     If invocation fails (non-zero exit, timeout) → return null, emit warning.

  2. Parse JSON. Extract:
     compilerOptions.declarationDir (preferred)
     compilerOptions.outDir (fallback)

  3. If neither set → return null (fall back to source).
     Emit warning: "tsconfig.json does not specify outDir or declarationDir. Falling back to source."

  4. Resolve directory path relative to tsconfigPath location.

  5. Verify directory exists and contains ≥1 .d.ts file.
     If not → return null (fall back to source).
     Emit warning: ".d.ts output not found. Falling back to source. Run: npm run build"

  Timeout: 15s for tsc --showConfig.
```

#### Export Condition Enumeration (new — applies to both paths)

```
EnumerateExportConditions(packageJsonPath):

Read package.json "exports" field.

CASE: "exports" absent → Legacy mode:
  Use pkg.types ?? pkg.typings ?? TS-extension-substitute(pkg.main).
  Single condition "default".
  If none resolve → error.

CASE: "exports" is a string → Single { condition: "default", path: <string> }.

CASE: "exports" is an object:
  For each key in the object:

    If key starts with "." → subpath export (e.g., ".", "./models").
      Recurse into value to extract conditions.

    If key == "types" → { condition: "default", path: <value> }

    If key is a PLATFORM CONDITION:
      ("node", "browser", "react-native", "electron", "deno",
       "bun", "worker", "edge-light")
      → Recurse into nested object:
        Extract "types" key → { condition: <key>, path: <value> }
        If no "types": look for "import" or "require" or "default"
          and apply TS extension substitution:
            .js  → .d.ts
            .mjs → .d.mts
            .cjs → .d.cts

    If key is a FORMAT CONDITION:
      ("import", "require", "module-sync", "default")
      → Apply TS extension substitution.
        Attach to current condition context (or "default").

  Deduplicate: if two conditions point to the same .d.ts file →
    merge into one entry with both condition names.

Result: list of { conditions: string[], subpath: string, dtsEntryPath: string }
```

#### Artifact Analysis Path

```
ArtifactPath(rootPath, artifacts, ct):
  For each (conditions, subpath, dtsEntryPath) from EnumerateExportConditions:
    1. Create ts-morph Project pointed at the .d.ts entry file.
    2. All export-ed declarations in .d.ts are public API by definition.
    3. Extract classes, interfaces, enums, type aliases, functions.
       *** Same extraction logic as source path ***
    4. Tag each module with conditions and exportPath.
    5. Dependencies fully resolve through node_modules .d.ts files.
```

#### Source Path (unchanged)

Existing `GraphAsync` logic: enumerate `.ts` files, create ts-morph project, extract API surface.

#### Schema Change

Add to TypeScript `ModuleInfo`:
```json
{
  "condition": "node",        // or "browser", "default", etc.
  "exportPath": ".",          // or "./models", "./client", etc.
  ...existing fields...
}
```

---

### Python — `PythonPublicApiGraphEngine`

The existing engine shells out to a Python script that uses AST parsing. The artifact path adds a `--mode=inspect` flag to the same script that uses `importlib` + `inspect` instead — **same subprocess, same output format, one flag**.

#### Import Name Derivation

The `--import-name` flag is optional. If omitted, the engine derives it automatically:

```
DeriveImportName(rootPath):
  1. Read pyproject.toml in rootPath → [project].name
     OR setup.cfg → [metadata].name
     OR setup.py → name= argument
  2. Normalize: replace "-" with "_", lowercase.
     e.g., "my-storage-sdk" → "my_storage_sdk"
  3. If no project metadata found → return null (source-only mode).
```

The caller can override with `--import-name <name>` if derivation produces the wrong result (e.g., namespace packages with different import names).

#### Artifact Resolution

```
ResolveArtifacts(importName):  // importName from --import-name or derived
  0. If `python` is not on PATH → return null, emit warning:
     "python not found. Falling back to source."

  1. Shell out: python -c "import <importName>; print('ok')"
     → If exit code 0 → package is importable, artifacts available.
     → If non-zero → return null (fall back to source).
     Emit warning: "Package '{importName}' not importable. Falling back to source. Run: pip install -e ."

  Timeout: 10s.
```

#### Artifact Analysis Path

```
ArtifactPath(rootPath, artifacts, ct):
  Run in subprocess using `python` (30s timeout):
    python graph_api.py --mode=inspect <import_name>

  Inside the script:
    1. importlib.import_module(import_name)
       If import fails → return error JSON with the exception message.
    2. getattr(mod, '__all__', None) → if exists, only those names are public.
       Otherwise, all names not starting with "_".
    3. For each public name:
       - inspect.getmembers() for classes and functions
       - typing.get_type_hints(obj) → fully resolved type annotations
         (handles "from __future__ import annotations", string annotations,
          TYPE_CHECKING blocks — all resolved at runtime)
       - inspect.signature(obj) → parameter names, defaults, annotations
       - inspect.getdoc(obj) → docstrings
       - type.__module__ → source package for dependency attribution (100% accurate)
    4. Handles C extensions (.so/.pyd) that AST cannot parse.

  Error handling:
    - 30s timeout on the subprocess. Kill and fall back to source if exceeded.
    - If the import raises an exception, catch it in the script and return
      a structured error. Engine falls back to source path.
    - The subprocess runs in a clean env (no inherited PYTHONPATH mutations).
```

#### Source Path (unchanged)

Existing `GraphAsync` logic: AST-based parsing via the Python script's current `--mode=ast` path.

---

### Java — `JavaPublicApiGraphEngine`

The existing engine shells out to a JBang script that uses JavaParser on `.java` source files. The artifact path adds a code branch in the same script: if `.class` files exist in the build output directory, use ASM `ClassReader` instead of JavaParser — **same JBang script, same output format**.

#### Artifact Resolution (from `--pom` or `--gradle` flag)

```
ResolveArtifacts(buildFilePath):  // provided by caller via --pom or --gradle
  0. If the required build tool (`mvn` or `gradle`) is not on PATH → return null, emit warning:
     "Build tool not found. Falling back to source."

  1. Maven path (--pom <pomXmlPath>):
     Shell out (30s timeout):
       mvn help:evaluate -Dexpression=project.build.outputDirectory -q -DforceStdout -f <pomXmlPath>
     → Returns ABSOLUTE path to compiled classes directory.
     If invocation fails (non-zero exit, timeout) → return null, emit warning.

  2. Gradle path (--gradle <buildGradlePath>):
     Shell out (30s timeout) with init script:
       gradle -q -I <initScript> printBuildDir -b <buildGradlePath>
       where initScript registers a task:
         task printBuildDir { println layout.buildDirectory.get().asFile.absolutePath }
       Append /classes/java/main to the result.

  3. Verify the resolved directory exists and contains ≥1 .class file.
     If not → return null (fall back to source).
     Emit warning: "No .class files found. Falling back to source. Run: mvn compile / gradle compileJava"
```

#### Artifact Analysis Path

```
ArtifactPath(rootPath, artifacts, ct):
  Run via JBang script with --mode=compiled --classes-dir=<classesDir>:

  1. Add //DEPS org.ow2.asm:asm:9.7 to JBang header.

  2. For each .class file in the classes directory:
     ClassReader(bytes) + ClassVisitor:
       visit()        → class name, superclass, interfaces, access flags, generic signature
       visitMethod()  → method name, descriptor, generic signature, exceptions, access flags
       visitField()   → field name, descriptor, generic signature, access flags

  3. Use ASM SignatureReader + custom SignatureVisitor to parse generic type
     signatures into readable form:
       Ljava/util/List<Ljava/lang/String;>;  →  List<String>

  4. Filter to ACC_PUBLIC access flag only.

  5. Map internal names (com/example/storage/blob/BlobClient)
     to dotted names (com.example.storage.blob.BlobClient).

  6. Javadoc pairing: parse source .java files with JavaParser
     (existing dependency) for Javadoc parsing ONLY.
     Match by fully-qualified class + method name + parameter types.
```

#### Source Path (unchanged)

Existing `GraphAsync` logic: parse `.java` files with JavaParser.

---

### Go — `GoPublicApiGraphEngine`

**No changes needed.** The existing engine already type-checks via `packages.Load()`, which resolves all types fully. Go has no separate compiled artifact. The engine already achieves the "compiled-mode" quality level.

The only improvement is converting the engine from a single-file script to a Go module for `golang.org/x/tools` dependency management:

```
1. Convert engine from single-file script to Go module:
   Add go.mod with golang.org/x/tools dependency.
   Update GoPublicApiGraphEngine.cs to "go build" the module directory.

2. packages.Load() already uses:
   Mode: packages.NeedName | packages.NeedTypes | packages.NeedSyntax | packages.NeedTypesInfo
   → Full type resolution. Cross-package types resolve automatically.
```

---

## Consumer Updates

### PackageInfoService

- Factory lambdas in `AnalyzerRegistry` gain an `ArtifactOptions?` parameter (see Registry section above).
- `PackageInfoService.GraphPublicApiAsync` accepts an optional `ArtifactOptions` and passes it through to the factory.
- Engines emit a diagnostic indicating which input path was taken (artifact or source).

### CLI (CliOptions in PublicApiGraphEngine.Contracts)

- Add optional artifact parameters to `CliOptions`:
  - `--csproj <file>` (C#)
  - `--tsconfig <file>` + `--package-json <file>` (TypeScript)
  - `--pom <file>` (Java/Maven)
  - `--gradle <file>` (Java/Gradle)
  - `--import-name <name>` (Python)
- `Program.cs` for each engine reads the relevant flag and passes it to the engine constructor.
- No `--engine-mode` flag needed — presence of the artifact flag is the signal.

### CLI (Microsoft.SdkChat — top-level `package api graph`)

- Add optional `--csproj`, `--tsconfig`, `--package-json`, `--pom`, `--gradle`, `--import-name` options.
- Pass them through `PackageInfoService` into the engine constructor.
- When omitted, current source-only behavior is preserved exactly.

**Auto-discovery via SdkInfo:** `SdkInfo.ScanAsync` already detects and stores `BuildFilePath` (e.g., the `.csproj`, `pom.xml`, `tsconfig.json`, `pyproject.toml`) as part of its `BuildMarker` scan. Expose this as a public property on `SdkInfo`. Then `PackageInfoService` can auto-populate `ArtifactOptions` from `SdkInfo.BuildFilePath` when the user doesn't provide explicit flags:

```csharp
// In PackageInfoService.GraphPublicApiAsync:
var sdkInfo = await SdkInfo.ScanAsync(packagePath, ct);
var artifactOpts = explicitOptions ?? ArtifactOptions.FromSdkInfo(sdkInfo);
// artifactOpts is null if SdkInfo didn't find a build file
```

This gives the best of both worlds:
- **Standalone engine CLIs**: caller must provide explicit flags (no ambiguity).
- **Top-level `package api graph` CLI**: auto-discovers from `SdkInfo` (good UX), overridable with explicit flags.

### MCP Tools (ApiMcpTools, SamplesMcpTools)

- Add optional `csprojPath`, `tsconfigPath`, `packageJsonPath`, `pomPath`, `gradlePath`, `importName` parameters to MCP tool schemas.
- When provided, pass through to `PackageInfoService` as `ArtifactOptions`.
- When omitted, `PackageInfoService` auto-discovers via `SdkInfo.BuildFilePath` (same as top-level CLI).
- The AI caller (LLM) can discover these paths from the workspace context and pass them. If it doesn't, source-only fallback applies silently.

### Cache Updates

The existing engine cache continues to work. Cache key gains an `inputPath` discriminator (artifact vs. source) so repeated runs with the same artifacts don't re-analyze:

| Language | Source mode fingerprints | Artifact mode fingerprints |
|----------|------------------------|---------------------------|
| **C#** | `*.cs` files (existing) | The `.dll` file + `.xml` doc sidecar |
| **TypeScript** | `*.ts` files (existing) | All `.d.ts` files in the output directory |
| **Python** | `*.py` files (existing) | `*.py` files in the installed package path |
| **Java** | `*.java` files (existing) | All `.class` files in the output directory |
| **Go** | `*.go` files (existing, unchanged) | N/A — same files |

### Formatter Updates

TypeScript `ModuleInfo` gains `condition` and `exportPath` fields. Formatters updated to render them in stubs output. No other schema changes — engines produce the same `ApiIndex` types, just with higher-fidelity type information when artifacts are available.

### ReachabilityAnalyzer / SignatureTokenizer

No changes expected — the `TypeNode` graph and `SignatureTokenizer` operate on the same `ApiIndex` schema. If artifact-based analysis produces richer type strings (e.g., fully-qualified generic constraints), `SignatureTokenizer` handles them already since it splits on non-identifier characters.

---

## Accuracy Measurement

### Before/After Validation

For each language, run the engine on the same SDK with and without the artifact flag and compare:

```
// Source mode (no artifact flag)
source_result  = engine <sdkRoot> --json

// Artifact mode (explicit flag)
artifact_result = engine <sdkRoot> --json --csproj <path>   // (or --pom, --tsconfig, etc.)

diff = compare(source_result, artifact_result)
```

Metrics per SDK:
- **Unresolved types**: count of types in the output that contain `?`, `object`, `unknown`, or are flagged `IsError`
- **Type accuracy delta**: types that differ between source and artifact output (artifact should be a strict superset of accuracy)
- **Missing members**: members present in one output but absent in the other
- **Dependency completeness**: dependencies detected by artifact path but missed by source path

Track across a corpus of SDK packages for each language. This is the competitive moat metric.

---

## Testing Strategy

### Artifact Mode Tests

- Small SDK fixture projects per language, pre-built in the test fixture setup (build is a test prerequisite, not done by the engine).
- Verify: 0 unresolved types, 0 `TypeKind.Error`, 0 missing dependency entries.
- TypeScript multi-target: fixture with `node`/`browser` conditions → verify separate module entries.

### Fallback Tests

- Delete build output from fixture → pass the artifact flag anyway → verify engine transparently falls back to source analysis with a warning diagnostic.
- Omit the artifact flag entirely → verify current behavior unchanged.

### Accuracy Comparison Tests

- Run engine on each fixture without artifact flag and with artifact flag → verify artifact output is a superset of source output in type accuracy.

### Existing Tests

- All existing tests remain unchanged and must pass.
- Source-parsing code paths are not modified, only wrapped in a method for the fallback path.

---

## Implementation Order

| Phase | Scope | Why first |
|-------|-------|-----------|
| **1** | `ArtifactOptions` record + `CliOptions` artifact flags + registry signature change + `SdkInfo.BuildFilePath` exposure + MCP parameter additions | Infrastructure plumbing. All engines accept artifact paths through constructors but ignore them until their implementation lands. No behavior change yet. |
| **2** | Go engine: convert to Go module | Lowest risk. No artifact flag needed (Go already type-checks). Structural improvement only. |
| **3** | C# engine: implement `--csproj` artifact path | Low risk. Roslyn is already a dependency. The symbol-walking code is shared — only the compilation input changes (DLL metadata refs vs. syntax trees). |
| **4** | Java engine: implement `--pom` / `--gradle` artifact path | Medium. ASM `ClassReader`/`SignatureVisitor` for generics is the main new code. Single engine, one new code branch in the JBang script. |
| **5** | TypeScript engine: implement `--tsconfig` / `--package-json` artifact path + export conditions | Medium-high. Export condition enumeration has many edge cases. ts-morph usage on `.d.ts` is straightforward. |
| **6** | Python engine: implement `--import-name` artifact path + auto-derivation | Highest risk. Runtime import can trigger side effects. Subprocess isolation and timeout handling require care. Falls back to source (current AST path) on any failure. |

Each phase ships independently. Users get artifact-based analysis as soon as each language lands — no big bang.

---

## Summary: Artifact Resolution

| Language | CLI Flag | Build Tool Query | Fallback trigger |
|----------|----------|------------------|------------------|
| **C#** | `--csproj <file>` | `dotnet msbuild <csproj> -getProperty:TargetPath -target:GetTargetPath` | DLL not found at resolved path |
| **TypeScript** | `--tsconfig <file>` + `--package-json <file>` | `tsc -p <tsconfig> --showConfig` → parse `declarationDir`/`outDir` | No `.d.ts` files in output dir |
| **Python** | `--import-name <name>` | `python -c "import <name>"` — check if importable | Import fails (package not installed) |
| **Java (Maven)** | `--pom <file>` | `mvn help:evaluate -Dexpression=project.build.outputDirectory` | No `.class` files in output dir |
| **Java (Gradle)** | `--gradle <file>` | `gradle -q -I <initScript> printBuildDir` + `/classes/java/main` | No `.class` files in output dir |
| **Go** | None needed | N/A — `packages.Load()` type-checks directly | N/A |

All queries delegate to the build system itself — zero heuristics. When artifacts are absent, the engine falls back to source analysis with a warning diagnostic.

---

## Key Decisions

| Decision | Rationale |
|----------|-----------|
| Enhance existing engines, don't create parallel ones | The compiled-artifact path is a better *input* to the same analysis pipeline, not a separate pipeline. Avoids code duplication, keeps one engine per language, eliminates `CompositeEngine` abstraction. ~30% of the code vs. the parallel-engine approach. |
| No `CompositeEngine` | Fallback logic is trivial (null-check on artifact resolution) and belongs inside each engine, not in a separate orchestrator. One less abstraction to maintain and test. |
| No new interfaces or engine classes | `IPublicApiGraphEngine<TIndex>` is unchanged. The enhancement is internal to each engine's `GraphAsync`. `AnalyzerRegistry` gains an `ArtifactOptions?` parameter but no new engine types. |
| Explicit artifact paths at engine boundary | The engine contract takes concrete paths (`--csproj`, `--pom`, etc.) — no ambiguity, no auto-detection inside the engine. Avoids "which `.csproj`?" problems. |
| Auto-discovery at top-level CLI / MCP | `SdkInfo.BuildFilePath` (already detected during scan) populates `ArtifactOptions` automatically for the top-level CLI and MCP tools. Users don't need to pass flags manually in the common case. Explicit flags override auto-discovery. |
| Fallback to source when artifacts absent | If an artifact flag is provided but the build hasn't been run, the engine warns and falls back to source parsing. No hard failure — the user still gets results. |
| Graceful handling of missing build tools | If `dotnet`, `tsc`, `mvn`, `gradle`, or `python` isn't on PATH when the artifact flag is given, the engine warns and falls back to source. No crash. |
| Python import name is derivable | The engine reads `pyproject.toml` / `setup.cfg` to derive the import name automatically. `--import-name` is only needed to override when derivation gives the wrong result. |
| Build system queries over config parsing | `dotnet msbuild`, `tsc --showConfig`, `mvn help:evaluate` evaluate their own config chains — we don't re-implement resolution logic. |
| Go needs no changes | Already achieves "compiled" quality via `packages.Load()`. Only structural improvement (single-file script → Go module). |
| Gradle `layout.buildDirectory` via init script | `buildDir` property is deprecated since Gradle 8.x. Init script approach is forward-compatible and avoids parsing deprecation warnings from stdout. |
| Timeouts on all shell-outs | 30s default. Prevents hung Maven/Gradle/tsc processes from blocking the engine. On timeout, falls back to source path. |
| Ship incrementally by language | Each language ships independently. Go first (lowest risk), Python last (highest risk). Users get value as each lands. |
| Diff-based accuracy metric | Run artifact vs. source on the same SDK, measure unresolved types / total types. This is the quality gate. |
