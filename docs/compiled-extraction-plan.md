# Public API Graph Engine — Design Plan

## Overview

Add 5 artifact-backed Public API Graph Engine extractors alongside the existing source-parsing extractors. A `CompositeExtractor` orchestrates the two tiers with explicit strategy selection. **Preconditions:** the SDK is already built and all dependencies are installed — building is out of scope. Each Graph Engine extractor receives strongly-typed, language-specific inputs (project file paths) from the caller. No heuristics. No file discovery. No path guessing. TypeScript gains multi-target export condition support (node/browser/etc.).

### Goals

- **100% type accuracy**: every type in every public API signature fully resolves — no `TypeKind.Error`, no missing imports, no string-based guessing.
- **Process the artifact, not the source**: extract the public API of the produced output (DLLs, `.d.ts`, `.class` files, installed packages, type-checked Go packages) — the API consumers actually see.
- **Lean interface**: Graph Engine extractors implement the same `IApiExtractor<TIndex>` interface as source extractors. No new interfaces; orchestration logic lives in `CompositeExtractor`, not in the extractor contract.
- **Zero heuristics**: all inputs (project file paths) are explicit parameters from the caller. Build output is resolved by querying the build system itself.

### Non-Goals

- Modifying existing source-parsing extractors (they remain as-is for fallback).
- Building SDKs, installing dependencies, or downloading packages.
- Discovering project files — that's the caller's responsibility.

### Preconditions (caller must satisfy)

| Language | What must be done before calling the Graph Engine extractor |
|----------|-------------------------------------------------------|
| **C#** | `dotnet build <csproj>` (produces DLL + XML docs) |
| **TypeScript** | `npm install && npm run build` (produces `.d.ts` files, populates `node_modules`) |
| **Python** | `pip install -e .` (makes package importable via `python`) |
| **Java (Maven)** | `mvn compile -f <pom.xml>` (produces `.class` files) |
| **Java (Gradle)** | `gradle compileJava -b <build.gradle>` (produces `.class` files) |
| **Go** | `go mod download` (fetches dependencies; `packages.Load()` type-checks on the fly) |

---

## Architecture

### Extraction Strategy

```
ExtractionStrategy enum:
  Compiled  — only Graph Engine extractor. Error if build output missing.
  Source    — only source extractor. Current behavior.
  Auto     — try Compiled → fall back to Source if build output absent. (default)
```

Controlled via:
- CLI: `--extraction-mode compiled|source|auto`
- Env var: `SDK_CHAT_EXTRACTION_MODE`
- MCP tool parameter

### Interface — No Changes

Graph Engine extractors implement the existing `IApiExtractor<TIndex>`. No new interface. The `rootPath` parameter carries the project file path (for C#: the `.csproj` path; for Go: the `go.mod` directory; etc.).

```
IApiExtractor<TIndex>                    (existing — unchanged)
├── ExtractAsync(rootPath, ct) → ExtractorResult<TIndex>
├── Language: string
├── IsAvailable(): bool
├── ToJson(index) → string
└── ToStubs(index) → string
```

TypeScript requires two paths, so it accepts them via constructor parameters:

```csharp
// TypeScript: two paths required
new TypeScriptCompiledExtractor(tsconfigPath, packageJsonPath)
```

The `rootPath` in `ExtractAsync` is still the SDK root directory. Constructor-injected paths tell the extractor *which* project files and tools to use.

### CompositeExtractor

Wraps a compiled `IApiExtractor<TIndex>` and a source `IApiExtractor<TIndex>`:

```
CompositeExtractor<TIndex>(compiled, source)

ExtractAsync(rootPath, ct):
  strategy = resolve from CLI/env/parameter

  if strategy == Source:
    return source.ExtractAsync(rootPath, ct)

  result = compiled.ExtractAsync(rootPath, ct)

  if result.IsSuccess:
    return result

  if strategy == Compiled:
    return result  // propagate the failure as-is

  // strategy == Auto
  log warning: "Graph Engine extraction failed: {result.Error}. Falling back to source."
  return source.ExtractAsync(rootPath, ct)
```

No `LocateBuildOutput`. No `GetBuildCommands`. The Graph Engine extractor either succeeds or returns `Failure` with a descriptive error. The `CompositeExtractor` makes the fallback decision.

### Build Layout Cache (new)

To avoid repeated build-tool startup for output-layout resolution, Graph Engine extractors use a shared `BuildLayoutCache`.

```
BuildLayoutCache
  Key:
    - language/runtime (dotnet | typescript | java-maven | java-gradle)
    - normalized project file path

  Value:
    - outputDirectory (absolute path)
    - configFingerprint
    - createdAtUtc
```

Resolution flow:

```
ResolveOutputDirectory(projectFilePath):
  1) Compute configFingerprint from build configuration files.
  2) cache.TryGet(key)
     - hit and fingerprint matches -> return cached outputDirectory
     - miss or fingerprint mismatch -> shell out once to build tool, cache result, return
```

Invalidation is fingerprint-based (file metadata hash), not TTL-based:

- **.NET fingerprint set**: `<project>.csproj`, nearest `Directory.Build.props/targets`, `global.json`, `NuGet.Config`, `Directory.Packages.props` when present.
- **TypeScript fingerprint set**: `tsconfig.json` (and resolved local `extends` chain files), `package.json`, lockfile (`package-lock.json|pnpm-lock.yaml|yarn.lock`) when present.
- **Maven fingerprint set**: `pom.xml`, `.mvn/maven.config`, `.mvn/jvm.config`, sibling parent POMs in the same repo when present.
- **Gradle fingerprint set**: `build.gradle|build.gradle.kts`, `settings.gradle|settings.gradle.kts`, `gradle.properties`, `gradle/wrapper/gradle-wrapper.properties`, `gradle/libs.versions.toml`.

Validation guard:

```
If cached outputDirectory no longer exists or has zero expected artifacts:
  evict cache entry
  perform one fresh build-tool query
```

Expected artifacts by runtime:
- **.NET**: resolved `.dll`
- **TypeScript**: resolved declaration directory has `.d.ts`
- **Java**: resolved classes directory has `.class`

This guarantees "query once" behavior across repeated CLI invocations while still invalidating immediately on config changes.

### Registry Update (PackageInfoService)

```csharp
// projectFiles resolved by caller before reaching the registry
AnalyzerRegistry = {
    [DotNet]      = (pf) => new CompositeExtractor(
                        new CSharpCompiledExtractor(pf.CsprojPath),
                        new CSharpApiExtractor()),
    [Python]      = (pf) => new CompositeExtractor(
                        new PythonCompiledExtractor(),
                        new PythonApiExtractor()),
    [TypeScript]  = (pf) => new CompositeExtractor(
                        new TypeScriptCompiledExtractor(pf.TsconfigPath, pf.PackageJsonPath),
                        new TypeScriptApiExtractor()),
    [JavaScript]  = (pf) => new CompositeExtractor(
                        new TypeScriptCompiledExtractor(pf.TsconfigPath, pf.PackageJsonPath),
                        new TypeScriptApiExtractor()),
    [Java+Maven]  = (pf) => new CompositeExtractor(
                        new JavaMavenCompiledExtractor(pf.PomXmlPath),
                        new JavaApiExtractor()),
    [Java+Gradle] = (pf) => new CompositeExtractor(
                        new JavaGradleCompiledExtractor(pf.BuildGradlePath),
                        new JavaApiExtractor()),
    [Go]          = (pf) => new CompositeExtractor(
                        new GoCompiledExtractor(),
                        new GoApiExtractor()),
};
```

---

## Per-Language Public API Graph Engine Extractors

### C#/.NET — `CSharpCompiledExtractor`

**Constructor input:** `string csprojPath`

#### Build Output Resolution (internal)

```
1. Compute .NET config fingerprint.
2. Check BuildLayoutCache(dotnet, csprojPath):
  - fingerprint match -> use cached target path (no `dotnet msbuild` startup)
  - mismatch/miss -> continue

3. Shell out: dotnet msbuild <csprojPath> -getProperty:TargetPath -target:GetTargetPath
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

4. Persist { targetPath/outputDirectory, configFingerprint } in BuildLayoutCache.

5. If .dll does not exist at the resolved path:
   evict stale cache entry and return
   Failure("DLL not found at {path}. Run: dotnet build {csprojPath}")

6. XML doc sidecar: same directory, same filename with .xml extension.
   If absent → add warning (doc comments will be missing), continue.

Timeout: 30s for each msbuild invocation.
```

#### Extraction Algorithm

```
1. Load the SDK's DLL + all NuGet dependency DLLs into a CSharpCompilation:
   - MetadataReference.CreateFromFile(dllPath) — the SDK's own assembly
   - Runtime assembly refs (System.Runtime, etc.) — from AppContext.BaseDirectory
   - NuGet dependency DLLs — resolved from the .csproj's obj/project.assets.json
     (existing logic from CSharpApiExtractor.Dependencies.cs)

2. Walk compilation.GlobalNamespace recursively.
   For each INamedTypeSymbol where DeclaredAccessibility == Public:
     - GetMembers() → constructors, methods, properties, events, indexers, operators
     - Fully resolved generic type parameters, nullable annotations
     - async detection from return type (Task/ValueTask/IAsyncEnumerable)

3. Doc comments: compute XML doc ID for each member (M:Namespace.Type.Method(ParamType))
   and look up in the parsed .xml sidecar file.

4. Dependencies: every ITypeSymbol in a public signature whose
   ContainingAssembly.Name ≠ SDK's own assembly → external dependency.
   Group by assembly name. 100% accurate — no string parsing.
```

---

### TypeScript — `TypeScriptCompiledExtractor`

**Constructor inputs:** `string tsconfigPath`, `string packageJsonPath`

#### Build Output Resolution (internal)

```
1. Compute TypeScript config fingerprint.
2. Check BuildLayoutCache(typescript, tsconfigPath):
  - fingerprint match -> use cached declaration output directory (no `tsc` startup)
  - mismatch/miss -> continue

3. Shell out: tsc -p <tsconfigPath> --showConfig
   → Returns fully merged config as JSON (all "extends" chains resolved).
   Requires node_modules to be populated (extends from packages must resolve).

4. Parse JSON. Extract:
   compilerOptions.declarationDir (preferred)
   compilerOptions.outDir (fallback)

5. If neither set → Failure("tsconfig.json must specify outDir or declarationDir")

6. Resolve directory path relative to tsconfigPath location.

7. Persist { outputDirectory, configFingerprint } in BuildLayoutCache.

8. Verify directory exists and contains ≥1 .d.ts file.
   If not → Failure(".d.ts output not found at {dir}. Run: npm run build")

Timeout: 15s for tsc --showConfig.
```

#### Export Condition Enumeration — `EnumerateExportConditions()`

```
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

#### Extraction Algorithm

```
For each (conditions, subpath, dtsEntryPath):
  1. Create ts-morph Project pointed at the .d.ts entry file.
  2. All export-ed declarations in .d.ts are public API by definition.
  3. Extract classes, interfaces, enums, type aliases, functions.
  4. Tag each module with conditions and exportPath.
  5. Dependencies fully resolve through node_modules .d.ts files.
```

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

### Python — `PythonCompiledExtractor`

**Constructor inputs:** none. Uses `python` from PATH (the standard executable name). The package must already be importable in this interpreter's environment (i.e., `pip install -e .` has been run).

The `rootPath` parameter is the SDK root directory. The extractor derives the import name from the project metadata in that directory.

#### Extraction Algorithm

```
Run in subprocess using `python` (30s timeout):
  python extract_api.py --mode=inspect <import_name>

The import name is derived from the SDK root:
  Read pyproject.toml/setup.cfg/setup.py in rootPath for [project].name,
  normalize to import name (replace "-" with "_", lowercase).

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
  - 30s timeout on the subprocess. Kill and return Failure if exceeded.
  - If the import raises an exception, catch it in the script and return
    a structured error (not a crash).
  - The subprocess runs in a clean env (no inherited PYTHONPATH mutations).
```

---

### Java (Maven) — `JavaMavenCompiledExtractor`

**Constructor input:** `string pomXmlPath` — path to `pom.xml`.

#### Build Output Resolution (internal)

```
1. Compute Maven config fingerprint.
2. Check BuildLayoutCache(java-maven, pomXmlPath):
  - fingerprint match -> use cached output directory (no JVM startup)
  - mismatch/miss -> continue

3. Shell out once (30s timeout):
  mvn help:evaluate -Dexpression=project.build.outputDirectory -q -DforceStdout -f <pomXmlPath>
  → Returns ABSOLUTE path to compiled classes directory.
  All Maven property interpolation, parent POM inheritance,
  and profile activation resolved by Maven itself.

4. Persist { outputDirectory, configFingerprint } in BuildLayoutCache.

Verify directory exists and contains ≥1 .class file.
If not → Failure("No .class files in {dir}. Run: mvn compile -f {pomXmlPath}")
```

---

### Java (Gradle) — `JavaGradleCompiledExtractor`

**Constructor input:** `string buildGradlePath` — path to `build.gradle` or `build.gradle.kts`.

#### Build Output Resolution (internal)

```
1. Compute Gradle config fingerprint.
2. Check BuildLayoutCache(java-gradle, buildGradlePath):
   - fingerprint match -> use cached output directory (no JVM startup)
   - mismatch/miss -> continue

3. Shell out once (30s timeout) with init script to query layout.buildDirectory:
   gradle -q -I <initScript> printBuildDir -b <buildGradlePath>
   where initScript registers a task:
     task printBuildDir { println layout.buildDirectory.get().asFile.absolutePath }
   Append /classes/java/main to the result.

4. Persist { outputDirectory, configFingerprint } in BuildLayoutCache.

Verify directory exists and contains ≥1 .class file.
If not → Failure("No .class files in {dir}. Run: gradle compileJava -b {buildGradlePath}")
```

### Java — Shared Extraction Algorithm

Both Maven and Gradle extractors share the same extraction algorithm once the classes directory is resolved:

```
1. Add //DEPS org.ow2.asm:asm:9.7 to JBang header.

2. For each .class file in the output directory:
   ClassReader(bytes) + ClassVisitor:
     visit()        → class name, superclass, interfaces, access flags, generic signature
     visitMethod()  → method name, descriptor, generic signature, exceptions, access flags
     visitField()   → field name, descriptor, generic signature, access flags

3. Use ASM SignatureReader + custom SignatureVisitor to parse generic type
   signatures into readable form:
     Ljava/util/List<Ljava/lang/String;>;  →  List<String>

4. Filter to ACC_PUBLIC access flag only.

5. Map internal names (com/azure/storage/blob/BlobClient)
   to dotted names (com.azure.storage.blob.BlobClient).

6. Javadoc pairing: parse source .java files with JavaParser
   (existing dependency) for Javadoc extraction ONLY.
   Match by fully-qualified class + method name + parameter types.
```

---

### Go — `GoCompiledExtractor`

**Constructor inputs:** none. The `rootPath` parameter to `ExtractAsync` is the directory containing `go.mod`.

#### Extraction Algorithm

```
1. Convert extractor from single-file script to Go module:
   Add go.mod with golang.org/x/tools dependency.
   Update GoApiExtractor.cs to "go build" the module directory.

2. Use packages.Load() with config:
   Mode: packages.NeedName | packages.NeedTypes | packages.NeedSyntax | packages.NeedTypesInfo
   Dir: rootPath (directory containing go.mod)
   Pattern: ./...

   If packages.Load() returns errors → Failure with the error messages.
   This is the ONLY verification — no separate `go build ./...` pre-check.

3. For each loaded package:
   Walk pkg.Types.Scope() → enumerate exported names via scope.Names()
   Filter: obj.Exported() == true

   For *types.TypeName:
     types.Named → NumMethods(), Method(i) for methods
     Underlying() for struct fields / interface methods

   For *types.Func:
     obj.Type().(*types.Signature) → Params(), Results() with fully resolved types

   For *types.Var:
     Package-level variables with resolved types

   For *types.Const:
     Constants with types and values

   Generic type params: types.TypeParam via Named.TypeParams() including constraints

4. Doc comments: go/doc on syntax trees (available via NeedSyntax).

5. Cross-package types resolve automatically by type checker.
   types.TypeString(typ, qualifier) with RelativeTo qualifier
   gives clean type names with package prefixes.
```

---

## Consumer Updates

### PackageInfoService

- `CompositeExtractor` wrappers pair each compiled + source extractor.
- `ExtractionStrategy` parameter propagates from CLI/env var/MCP.
- Factory lambdas accept project file paths and construct Graph Engine extractors with the right constructor args.
- When `Auto` fallback triggers, log warning with the Graph Engine extractor's error message.

### CLI (Program.cs)

- Add `--extraction-mode compiled|source|auto` option (default: `auto`).
- Add `SDK_CHAT_EXTRACTION_MODE` env var override.
- Surface in MCP tools as a parameter.

### Cache Updates

Per-language fingerprint targets:

| Language | Source mode fingerprints | Compiled mode fingerprints |
|----------|------------------------|---------------------------|
| **C#** | `*.cs` files | The `.dll` file + `.xml` doc sidecar |
| **TypeScript** | `*.ts` files | All `.d.ts` files in the output directory |
| **Python** | `*.py` files | `*.py` files in the installed package path |
| **Java** | `*.java` files | All `.class` files in the output directory |
| **Go** | `*.go` files | `*.go` files (same — Go has no separate artifact) |

`ExtractionCache`: cache key includes `(rootPath, extractionMode)` so compiled and source results don't collide.

`BuildLayoutCache` (new): caches resolved build-output locations for Graph Engine extractors.

- Key: `(language, projectFilePath)`
- Invalidation: config fingerprint mismatch or missing/empty expected artifacts
- Runtime coverage: `.NET` target path, `TypeScript` declaration output directory, `Java` classes directory
- Goal: no repeated `dotnet msbuild`/`tsc --showConfig`/`mvn`/`gradle` layout shell-outs on steady-state runs

### Formatter Updates

TypeScript `ModuleInfo` gains `condition` and `exportPath` fields. Formatters updated to render them in stubs output. No other schema changes — the Graph Engine extractors produce the same `ApiIndex` types as source extractors, just with higher-fidelity type information.

### ReachabilityAnalyzer / SignatureTokenizer

No changes expected — the `TypeNode` graph and `SignatureTokenizer` operate on the same `ApiIndex` schema. If Graph Engine extraction produces richer type strings (e.g., fully-qualified generic constraints), `SignatureTokenizer` handles them already since it splits on non-identifier characters.

---

## Accuracy Measurement

### Diff-Based Validation

For each language, run both extractors on the same SDK and diff the output:

```
source_result  = sourceExtractor.ExtractAsync(sdkRoot)
compiled_result = compiledExtractor.ExtractAsync(sdkRoot)
diff = compare(source_result, compiled_result)
```

Metrics per SDK:
- **Unresolved types**: count of types in the output that contain `?`, `object`, `unknown`, or are flagged `IsError`
- **Type accuracy delta**: types that differ between source and compiled output (compiled should be a strict superset of accuracy)
- **Missing members**: members present in one output but absent in the other
- **Dependency completeness**: dependencies detected by compiled but missed by source

Track across a corpus (e.g., all Azure SDK packages for each language). This is the competitive moat metric.

---

## Testing Strategy

### Compiled Mode Tests

- Small SDK fixture projects per language, pre-built in the test fixture setup (build is a test prerequisite, not done by the extractor).
- Verify: 0 unresolved types, 0 `TypeKind.Error`, 0 missing dependency entries.
- TypeScript multi-target: fixture with `node`/`browser` conditions → verify separate module entries.

### Fallback Tests

- Delete build output from fixture → verify `CompositeExtractor` in `Auto` mode activates source extractor with warning.
- `--extraction-mode compiled` without build → verify error returned (not a crash).
- `--extraction-mode source` → verify current behavior unchanged.

### Diff Tests

- Run both extractors on each fixture → verify compiled output is a superset of source output in type accuracy.

### Existing Tests

- All existing source-mode tests remain unchanged and must pass.
- Source extractors are not modified.

---

## Implementation Order

| Phase | Scope | Why first |
|-------|-------|-----------|
| **1** | `CompositeExtractor` + `ExtractionStrategy` enum + CLI flag + cache key update | Architecture plumbing — all subsequent extractors plug into this. |
| **1.5** | `BuildLayoutCache` + config fingerprint utility | Removes repeated build-layout shell-outs for .NET/TypeScript/Java before language extractors land. |
| **2** | `GoCompiledExtractor` | Lowest risk. `packages.Load()` does everything. No build artifact discovery. Existing Go extractor already shells out to Go toolchain. Proves the CompositeExtractor pattern end-to-end. |
| **3** | `CSharpCompiledExtractor` | Low risk. Roslyn is already a dependency. `dotnet msbuild` query is reliable. Validates the DLL-based extraction path. |
| **4** | `JavaMavenCompiledExtractor` + `JavaGradleCompiledExtractor` | Medium. ASM `SignatureVisitor` for generics is the main complexity. Two extractors but shared extraction algorithm — only build output resolution differs. |
| **5** | `TypeScriptCompiledExtractor` | Medium-high. Export condition enumeration has many edge cases. |
| **6** | `PythonCompiledExtractor` | Highest risk. Runtime import can trigger side effects. Subprocess isolation and timeout handling require care. |

Each phase ships independently. `Auto` mode means users get Graph Engine extraction as soon as each language lands — no big bang.

---

## Summary: Build Output Resolution

| Language | Build Tool Query | What It Returns |
|----------|-----------------|-----------------|
| **C#** | `dotnet msbuild <csproj> -getProperty:TargetPath -target:GetTargetPath` (cached via `BuildLayoutCache`) | Absolute path to `.dll` |
| **TypeScript** | `tsc -p <tsconfig> --showConfig` → parse `declarationDir`/`outDir` (cached via `BuildLayoutCache`) | Directory containing `.d.ts` files |
| **Python** | N/A — package must be importable via `python` | Runtime import via `importlib` |
| **Java (Maven)** | `mvn help:evaluate -Dexpression=project.build.outputDirectory -q -DforceStdout -f <pomXmlPath>` (cached via `BuildLayoutCache`) | Absolute path to `classes/` dir |
| **Java (Gradle)** | `gradle -q -I <initScript> printBuildDir -b <buildGradlePath>` + `/classes/java/main` (cached via `BuildLayoutCache`) | Absolute path to `classes/` dir |
| **Go** | N/A — `packages.Load()` type-checks directly | Type-checked packages (no file artifact) |

All queries delegate to the build system itself — zero heuristics.

---

## Key Decisions

| Decision | Rationale |
|----------|-----------|
| No new interface | Graph Engine extractors implement existing `IApiExtractor<TIndex>`. Constructor-injected config replaces method parameters. Less API surface, easier to test, no breaking changes. |
| `CompositeExtractor` as orchestrator | Fallback logic lives here, not in the extractors. Each extractor either succeeds or returns `Failure`. Clean separation. |
| Strongly-typed constructor inputs | Each extractor takes exactly the paths it needs — no union types, no filename-based dispatch. `CSharpCompiledExtractor(csprojPath)`, `TypeScriptCompiledExtractor(tsconfigPath, packageJsonPath)`, `JavaMavenCompiledExtractor(pomXmlPath)`, `JavaGradleCompiledExtractor(buildGradlePath)`, `PythonCompiledExtractor()`, `GoCompiledExtractor()`. The caller decides which extractor to instantiate. |
| Building is out of scope | The extractor assumes the SDK is built. If artifacts are missing, it returns `Failure` with a descriptive message. No `GetBuildCommands` method — the error message itself contains the command. |
| `python` from PATH | Assumes standard `python` executable exists. No venv detection, no interpreter path configuration. Caller ensures the right environment is active before invoking. |
| No `go build` pre-check for Go | `packages.Load()` is the single verification + extraction path. If it fails, that's the error. Simpler, more correct than a separate `go build ./...` step. |
| Gradle `layout.buildDirectory` via init script | `buildDir` property is deprecated since Gradle 8.x. Init script approach is forward-compatible and avoids parsing deprecation warnings from stdout. |
| Build system queries over config parsing | `dotnet msbuild`, `tsc --showConfig`, `mvn help:evaluate` evaluate their own config chains — we don't re-implement resolution logic. |
| Timeouts on all shell-outs | 30s default. Prevents hung Maven/Gradle/tsc processes from blocking the extraction pipeline. |
| Ship incrementally by language | Each language ships behind `Auto` mode. Go first (lowest risk), Python last (highest risk). Users get value as each lands. |
| Diff-based accuracy metric | Run compiled vs. source on the same SDK, measure unresolved types / total types. This is the quality gate. |
