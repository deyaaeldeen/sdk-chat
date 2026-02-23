# TypeScript Engine Redesign Plan

## Core Principle

**The compiler is the source of truth for all type information.**

Every place the current code tokenizes, regex-matches, or string-splits type text must be replaced with the equivalent TypeScript compiler API call. The compiler already resolves generics, tracks declarations, understands visibility, and performs module resolution — the engine should be a thin layer over those capabilities, not a parallel reimplementation.

---

## Phase 1: Introduce `ExtractionContext` (Low Risk)

**Problem:** The script uses module-level global singletons for type resolution:
- `discoveredBuiltins` (Set)
- `typeResolutionProject` (Project)
- `typeCollector` (TypeReferenceCollector)
- `pkgNameCache` (Map)

This prevents concurrent extractions, leaks state on exceptions, and is untestable.

**Solution:** Encapsulate all mutable state in an `ExtractionContext` class threaded through the call chain:

```typescript
class ExtractionContext {
  readonly project: Project;
  readonly checker: ts.TypeChecker;
  readonly builtinSymbols: Set<ts.Symbol>;
  readonly typeRefs: TypeReferenceCollector;
  readonly diagnostics: ExtractionDiagnostic[];
  private readonly pkgNameCache = new Map<string, string | undefined>();

  constructor(project: Project) {
    this.project = project;
    this.checker = project.getTypeChecker().compilerObject;
    this.builtinSymbols = this.discoverBuiltinSymbols();
    this.typeRefs = new TypeReferenceCollector(this);
    this.diagnostics = [];
  }

  /** Discover builtins by checking which symbols come from lib files */
  private discoverBuiltinSymbols(): Set<ts.Symbol> {
    const builtins = new Set<ts.Symbol>();
    for (const sf of this.project.getSourceFiles()) {
      if (sf.isDeclarationFile() && this.isLibFile(sf)) {
        for (const sym of sf.getSymbolsInScope(ts.SymbolFlags.Type)) {
          builtins.add(sym.compilerSymbol);
        }
      }
    }
    return builtins;
  }

  isBuiltin(symbol: ts.Symbol): boolean {
    return this.builtinSymbols.has(symbol);
  }

  isLibFile(sf: SourceFile): boolean {
    return sf.getFilePath().includes("/typescript/lib/");
  }

  resolvePackageName(filePath: string): string | undefined {
    // Cached directory-walk — the one place string-based path
    // logic is unavoidable (package.json is a filesystem concern,
    // not a type-system concern)
    let dir = path.dirname(filePath);
    const root = path.parse(dir).root;

    while (dir && dir !== root) {
      if (this.pkgNameCache.has(dir)) return this.pkgNameCache.get(dir);

      const pkgPath = path.join(dir, "package.json");
      if (fs.existsSync(pkgPath)) {
        try {
          const pkg = JSON.parse(fs.readFileSync(pkgPath, "utf-8"));
          const name: string | undefined = pkg.name;
          if (name) {
            this.pkgNameCache.set(dir, name);
            return name;
          }
        } catch { /* Malformed JSON — keep walking up */ }
      }
      dir = path.dirname(dir);
    }
    this.pkgNameCache.set(dir, undefined);
    return undefined;
  }
}
```

**Changes:**
- Create `ExtractionContext` class
- Add `ctx: ExtractionContext` as the first parameter to every extraction function
- Remove all module-level `let`/`const` singletons
- Construct context in `extractPackage`, pass through call chain

**Tests affected:** None — same behavior, pure refactor.

---

## Phase 2: Replace `simplifyType` with compiler `TypeFormatFlags` (Low Risk)

**Problem:** `simplifyType()` uses `type.replace(/import\([^)]+\)\./g, "")` to strip import prefixes. It's incomplete (doesn't handle nested imports) and fragile (regex doesn't handle nested parens).

**Solution:** Use the TypeChecker's own `typeToString` with display flags:

```typescript
function displayType(type: Type, ctx: ExtractionContext): string {
  return ctx.checker.typeToString(
    type.compilerType,
    /*enclosingDeclaration*/ undefined,
    ts.TypeFormatFlags.UseAliasDefinedOutsideCurrentScope
    | ts.TypeFormatFlags.UseFullyQualifiedType
    | ts.TypeFormatFlags.OmitParameterModifiers
  );
}
```

For annotation text:

```typescript
function getTypeText(
  node: { getTypeNode(): TypeNode | undefined; getType(): Type },
  ctx: ExtractionContext
): string {
  const typeNode = node.getTypeNode();
  if (typeNode) {
    // User wrote an explicit annotation — use the checker to print it
    return displayType(typeNode.getType(), ctx);
  }
  // Inferred type — let the checker display it
  return displayType(node.getType(), ctx);
}
```

**Changes:**
- Delete `simplifyType()` function entirely
- Replace all `simplifyType(type.getText())` calls with `displayType(type, ctx)`
- Replace all `simplifyType(typeText)` calls with `getTypeText(node, ctx)`

**Tests affected:** Type display assertions may change (should improve).

---

## Phase 3: Remove `collectRawTypeName` — compiler-only reference collection (Medium Risk)

**Problem:** Every extraction site calls both `collectFromType(type)` and `collectRawTypeName(typeText)`, where the latter does `typeText.split(/[<>|&,\[\]\s()]+/)` to tokenize strings. This dual-tracking is scattered across every extraction function, creating significant code duplication.

**Solution:** The `collectFromType` path (which uses the compiler's Type graph) is already the right approach. Replace the raw tokenizer with its import-declaration-based fallback (which is already in the code via `collectFromImportDeclarations`):

```typescript
class TypeReferenceCollector {
  private ctx: ExtractionContext;
  private refs = new Map<ts.Symbol, ResolvedTypeRef>();
  private definedSymbols = new Set<ts.Symbol>();
  private contextStack: string[] = [];
  private refsByContext = new Map<string, Set<ts.Symbol>>();

  constructor(ctx: ExtractionContext) { this.ctx = ctx; }

  collectFromType(type: Type): void {
    this.walkType(type.compilerType, new Set<number>());
  }

  private walkType(type: ts.Type, visited: Set<number>): void {
    const id = (type as any).id as number;
    if (visited.has(id)) return;
    visited.add(id);

    // Union
    if (type.isUnion()) {
      for (const member of type.types) this.walkType(member, visited);
      return;
    }

    // Intersection
    if (type.isIntersection()) {
      for (const member of type.types) this.walkType(member, visited);
      return;
    }

    // Check alias symbol first (preserves type alias references)
    const aliasSymbol = type.aliasSymbol;
    if (aliasSymbol && !this.ctx.isBuiltin(aliasSymbol)) {
      this.trackSymbol(aliasSymbol);
      if (type.aliasTypeArguments) {
        for (const arg of type.aliasTypeArguments) this.walkType(arg, visited);
      }
      return;
    }

    // Named type with symbol
    const symbol = type.getSymbol();
    if (symbol && !this.ctx.isBuiltin(symbol)) {
      if (this.isTypeDeclarationSymbol(symbol)) {
        this.trackSymbol(symbol);
      }
    }

    // Type arguments (generics)
    const typeArgs = this.ctx.checker.getTypeArguments(type as ts.TypeReference);
    if (typeArgs) {
      for (const arg of typeArgs) this.walkType(arg, visited);
    }

    // Base types
    const baseTypes = type.getBaseTypes?.();
    if (baseTypes) {
      for (const base of baseTypes) this.walkType(base, visited);
    }

    // Object type properties and signatures (for anonymous/inline types)
    if (type.flags & ts.TypeFlags.Object) {
      for (const prop of type.getProperties()) {
        const propType = this.ctx.checker.getTypeOfSymbol(prop);
        this.walkType(propType, visited);
      }
      for (const sig of type.getCallSignatures()) {
        this.walkSignature(sig, visited);
      }
      const stringIndex = type.getStringIndexType();
      if (stringIndex) this.walkType(stringIndex, visited);
      const numberIndex = type.getNumberIndexType();
      if (numberIndex) this.walkType(numberIndex, visited);
    }
  }

  private walkSignature(sig: ts.Signature, visited: Set<number>): void {
    for (const param of sig.getParameters()) {
      const paramType = this.ctx.checker.getTypeOfSymbol(param);
      this.walkType(paramType, visited);
    }
    this.walkType(sig.getReturnType(), visited);
  }

  private isTypeDeclarationSymbol(symbol: ts.Symbol): boolean {
    return (symbol.flags & (
      ts.SymbolFlags.Class | ts.SymbolFlags.Interface |
      ts.SymbolFlags.Enum | ts.SymbolFlags.TypeAlias
    )) !== 0;
  }

  private trackSymbol(symbol: ts.Symbol): void {
    if (this.definedSymbols.has(symbol)) return;
    if (this.refs.has(symbol)) return;

    const decl = symbol.getDeclarations()?.[0];
    if (!decl) return;

    const sf = decl.getSourceFile();
    if (this.ctx.isLibFile(sf)) return;

    const ref: ResolvedTypeRef = {
      name: symbol.getName(),
      symbol,
      sourceFile: sf,
      packageName: this.ctx.resolvePackageName(sf.fileName),
    };
    this.refs.set(symbol, ref);

    const ctx = this.currentContext();
    if (ctx) {
      if (!this.refsByContext.has(ctx)) this.refsByContext.set(ctx, new Set());
      this.refsByContext.get(ctx)!.add(symbol);
    }
  }
}
```

**Key insight:** By working with `ts.Symbol` objects (identity-based, not string-based), we never need string tokenization. A symbol IS its identity.

**Uninstalled packages fallback:** The compiler already parsed `import { Foo } from "pkg"` and gives us the symbol `Foo` even if its type is `any`. The existing `collectFromImportDeclarations` method handles this using the compiler's import resolution, not string tokenization. It becomes the sole fallback instead of `collectRawTypeName`.

**Changes:**
- Delete `collectRawTypeName()` method
- Delete all `collectRawTypeName(...)` calls from every extraction function
- Delete `rawTypeNames`, `rawTypeNamesByContext` from `TypeReferenceCollector`
- Delete `tokenizeInto()` in the engine section (keep the one in usage analysis — that operates on the already-extracted model, not the compiler AST)
- The `collectFromImportDeclarations` stays as the fallback path

**Tests affected:** Dependency tracking for uninstalled packages — verify import-declaration fallback catches all previously tokenizer-caught cases.

---

## Phase 4: Replace `any` casts with type guards (Low Risk)

**Problem:** Code frequently casts to `any` to call methods that might not exist: `(paramDecls[0] as any).getType?.()`. This loses type safety and hides bugs.

**Solution:** Use ts-morph's type guards exhaustively:

```typescript
function getTypeFromDeclaration(decl: Node): Type | undefined {
  if (Node.isParameterDeclaration(decl)) return decl.getType();
  if (Node.isPropertyDeclaration(decl)) return decl.getType();
  if (Node.isPropertySignature(decl)) return decl.getType();
  if (Node.isMethodDeclaration(decl)) return decl.getReturnType();
  if (Node.isMethodSignature(decl)) return decl.getReturnType();
  if (Node.isGetAccessorDeclaration(decl)) return decl.getReturnType();
  if (Node.isSetAccessorDeclaration(decl)) return decl.getParameters()[0]?.getType();
  if (Node.isVariableDeclaration(decl)) return decl.getType();
  if (Node.isTypeAliasDeclaration(decl)) return decl.getType();
  return undefined;
}

function getParametersFromDeclaration(decl: Node): ParameterDeclaration[] {
  if (Node.isMethodDeclaration(decl)) return decl.getParameters();
  if (Node.isMethodSignature(decl)) return decl.getParameters();
  if (Node.isFunctionDeclaration(decl)) return decl.getParameters();
  if (Node.isConstructorDeclaration(decl)) return decl.getParameters();
  if (Node.isArrowFunction(decl)) return decl.getParameters();
  return [];
}
```

**Changes:**
- Create `getTypeFromDeclaration()` and `getParametersFromDeclaration()` utility functions
- Replace all `(x as any).getType?.()` with `getTypeFromDeclaration(x)`
- Replace all `(x as any).getParameters?.()` with `getParametersFromDeclaration(x)`
- Replace all `(x as any).getReturnType?.()` with `getTypeFromDeclaration(x)` (for return types)

**Tests affected:** None — same behavior with type safety.

---

## Phase 5: Replace `_` filter with language-level visibility (Low Risk)

**Problem:** `.filter((m) => !m.getName().startsWith("_"))` is a naming convention heuristic, not a language feature. Public methods named `_transform` are silently excluded.

**Solution:** Use only what the language specifies:

```typescript
function isPublicMember(member: ClassMemberTypes): boolean {
  // TypeScript access modifiers
  if (member.getScope() === 'private' || member.getScope() === 'protected') {
    return false;
  }

  // ECMAScript private fields (#name)
  const name = member.getName();
  if (name.startsWith('#')) {
    return false;
  }

  // JSDoc @internal / @hidden — these are explicit author intent, not heuristics
  if (hasInternalOrHiddenTag(member as any)) {
    return false;
  }

  return true;
}
```

The `@internal`/`@hidden` check stays because it's an **explicit annotation** by the package author (used by TypeScript, API Extractor, and across the ecosystem). The `_` prefix filter is removed entirely.

**Changes:**
- Create `isPublicMember()` utility
- Replace all `.filter((m) => m.getScope() !== "private" && !m.getName().startsWith("_"))` with `.filter(isPublicMember)`
- Similarly for interface members (which don't have scope but can have `@internal`)

**Tests affected:** Some previously-excluded `_`-prefixed public members will now appear in output.

---

## Phase 6: Compiler-driven reachability analysis (Medium Risk)

**Problem:** `computeReachableTypes` builds its reference graph by tokenizing signature strings with `split(/[<>|&,\[\]\s(){}:;=]+/)`.

**Solution:** Build the reference graph using the `TypeReferenceCollector`'s context-scoped data, which was built from actual Symbol resolution during extraction:

```typescript
function computeReachableTypes(api: ApiIndex, ctx: ExtractionContext): Set<string> {
  // The TypeReferenceCollector already tracked context → referenced symbols.
  const graph = ctx.typeRefs.getContextGraph();
  // graph: Map<string, Set<string>> — typeName → set of referenced type names
  // Built from actual Symbol resolution, not string tokenization.

  const entryPoints = new Set<string>();
  for (const mod of api.modules) {
    for (const cls of mod.classes ?? []) if (cls.entryPoint) entryPoints.add(cls.name);
    for (const iface of mod.interfaces ?? []) if (iface.entryPoint) entryPoints.add(iface.name);
    for (const en of mod.enums ?? []) if (en.entryPoint) entryPoints.add(en.name);
    for (const t of mod.types ?? []) if (t.entryPoint) entryPoints.add(t.name);
    for (const fn of mod.functions ?? []) if (fn.entryPoint && fn.name) entryPoints.add(fn.name);
  }

  // BFS from entry points using compiler-resolved edges
  const reachable = new Set<string>();
  const queue = [...entryPoints];
  for (const ep of queue) reachable.add(ep);

  while (queue.length > 0) {
    const current = queue.shift()!;
    const refs = graph.get(current);
    if (refs) {
      for (const ref of refs) {
        if (!reachable.has(ref)) {
          reachable.add(ref);
          queue.push(ref);
        }
      }
    }
  }
  return reachable;
}
```

**Changes:**
- Add `getContextGraph(): Map<string, Set<string>>` to `TypeReferenceCollector`
- Replace `computeReachableTypes` body with BFS over symbol-resolved graph
- Delete `collectRefsFromText()` function

**Tests affected:** Reachability edge cases — verify same types are reachable with new approach.

---

## Phase 7: Replace path-mapping heuristics with module resolution (Medium Risk)

**Problem:** `resolveToSourceFile` uses hardcoded `dist/ → src/`, `lib/ → src/` directory mappings. Doesn't work for non-standard layouts.

**Solution:** Use the TypeScript compiler's own module resolution:

```typescript
function resolveEntryPointFiles(
  rootPath: string,
  project: Project,
  options: EngineOptions
): ExportEntry[] {
  const pkg = readPackageJson(rootPath, options);
  if (!pkg) return [];

  const exportPaths = extractExportPaths(pkg.exports);

  const entries: ExportEntry[] = [];
  for (const { exportPath, condition, filePath } of exportPaths) {
    // Let the project find the file — it already has module resolution configured
    const resolved = project.getSourceFile(path.resolve(rootPath, filePath));
    if (resolved) {
      entries.push({ exportPath, condition, filePath: resolved.getFilePath() });
      continue;
    }

    // Use TypeScript's own module resolution via tsconfig paths/rootDirs
    const resolvedModule = ts.resolveModuleName(
      filePath.replace(/^\.\//, ''),
      path.join(rootPath, 'package.json'),
      project.getCompilerOptions(),
      ts.sys
    );

    if (resolvedModule.resolvedModule) {
      const sf = project.getSourceFile(resolvedModule.resolvedModule.resolvedFileName);
      if (sf) {
        entries.push({ exportPath, condition, filePath: sf.getFilePath() });
      }
    }
  }

  return entries;
}
```

In compiled mode, the `.d.ts` files in the exports map are the actual input — no mapping needed. In source mode, if the project has a `tsconfig.json` with `paths` or `rootDirs`, the compiler's resolution handles the mapping. If neither works, the file isn't found, which is a diagnostic rather than a silent fallback.

**Changes:**
- Delete `resolveToSourceFile()` entirely
- Delete the hardcoded `mappings` array
- Replace with `ts.resolveModuleName` calls
- For compiled mode, resolve paths directly against the dtsRoot

**Tests affected:** Entry point detection for packages with non-standard layouts.

---

## Phase 8: Structured diagnostics instead of silent catch-all (Low Risk)

**Problem:** `catch { /* skip */ }` everywhere with no reporting. No way to know how many types were skipped during extraction.

**Solution:**

```typescript
class ExtractionDiagnostic {
  constructor(
    readonly level: 'info' | 'warning' | 'error',
    readonly code: string,
    readonly message: string,
    readonly sourceFile?: string,
    readonly typeName?: string,
  ) {}
}

function withDiagnostic<T>(
  ctx: ExtractionContext,
  operation: string,
  typeName: string,
  fn: () => T
): T | undefined {
  try {
    return fn();
  } catch (e) {
    ctx.diagnostics.push(new ExtractionDiagnostic(
      'warning',
      'EXTRACT_FAILED',
      `Failed to ${operation} for ${typeName}: ${e instanceof Error ? e.message : String(e)}`,
      undefined,
      typeName,
    ));
    return undefined;
  }
}
```

Diagnostics are collected on the context, serialized to stderr as structured JSON, and parsed by the C# host into `ApiDiagnostic` records.

**Changes:**
- Create `ExtractionDiagnostic` class and `withDiagnostic()` helper
- Replace all `catch { /* skip */ }` blocks with `withDiagnostic(ctx, ...)` calls
- Emit diagnostics to stderr as JSON before writing the main output to stdout
- Update C# `ParseStderrDiagnostics` to handle structured JSON diagnostics alongside plain text

**Tests affected:** New diagnostic output — add test assertions for diagnostic presence.

---

## Phase 9: Lazy dependency resolution (Low Risk)

**Problem:** `project.addSourceFilesAtPaths(path.join(dir, "**/*.d.ts"))` for every dependency directory. O(all files in all deps) instead of O(referenced files).

**Solution:** Add files on-demand when resolving dependency types:

```typescript
function resolveExternalType(
  symbol: ts.Symbol,
  ctx: ExtractionContext
): SourceFile | undefined {
  const decl = symbol.getDeclarations()?.[0];
  if (!decl) return undefined;

  const fileName = decl.getSourceFile().fileName;

  // Check if already in project
  let sf = ctx.project.getSourceFile(fileName);
  if (sf) return sf;

  // Add just this file
  try {
    sf = ctx.project.addSourceFileAtPath(fileName);
    return sf;
  } catch {
    ctx.diagnostics.push(new ExtractionDiagnostic(
      'warning', 'DEP_RESOLVE', `Cannot add dependency file: ${fileName}`
    ));
    return undefined;
  }
}
```

**Changes:**
- Delete `project.addSourceFilesAtPaths(path.join(dir, "**/*.d.ts"))` glob in `resolveTransitiveDependencies`
- Replace with on-demand `addSourceFileAtPath` for each resolved dependency type
- Remove `addedFiles` set tracking

**Tests affected:** Performance improvement, same behavior.

---

## Phase 10: Add overloads, index signatures, accessors (Low Risk, Additive)

**Problem:** The engine doesn't handle TypeScript-specific patterns properly: method overloads are not linked, index signatures are omitted, get/set accessors aren't distinguished.

**Solution:**

### Overloads
```typescript
function extractMethodWithOverloads(
  method: MethodDeclaration,
  ctx: ExtractionContext
): MethodInfo {
  const overloads = method.getOverloads();
  if (overloads.length > 0) {
    // Each overload is a distinct signature
    return {
      name: method.getName(),
      overloads: overloads.map(o => extractSingleSignature(o, ctx)),
      ...
    };
  }
  return extractSingleSignature(method, ctx);
}
```

### Index Signatures
```typescript
function extractIndexSignatures(
  type: InterfaceDeclaration | ClassDeclaration,
  ctx: ExtractionContext
): IndexSignatureInfo[] {
  return type.getIndexSignatures().map(sig => ({
    keyType: displayType(sig.getKeyType(), ctx),
    valueType: displayType(sig.getReturnType(), ctx),
    readonly: sig.isReadonly(),
  }));
}
```

### Accessors
```typescript
function extractAccessors(
  cls: ClassDeclaration,
  ctx: ExtractionContext
): PropertyInfo[] {
  const getAccessors = cls.getGetAccessors().filter(a => isPublicMember(a));
  const setAccessors = new Set(cls.getSetAccessors().map(s => s.getName()));

  return getAccessors.map(getter => ({
    name: getter.getName(),
    type: displayType(getter.getReturnType(), ctx),
    readonly: !setAccessors.has(getter.getName()),
  }));
}
```

**Changes:**
- Add `IndexSignatureInfo` to the models (both TypeScript and C#)
- Add optional `overloads` field to `MethodInfo`
- Extract accessors alongside properties in `extractClass`
- Update `TypeScriptFormatter.cs` to render index signatures

**Tests affected:** New model fields — add assertions for index signatures, overloads, accessors.

---

## Phase 11: Condition normalization — preserve, don't collapse (Low Risk)

**Problem:** `normalizeCondition` collapses `"import|types"` → `"import"`, losing the `types` qualifier.

**Solution:** Preserve the full condition chain as structured data:

```typescript
interface ExportCondition {
  /** The full chain of conditions, e.g. ["import", "types"] */
  chain: string[];
  /** The primary condition for grouping (last environment condition) */
  primary: string;
}
```

The C# model `Condition` continues to use the primary for backward compatibility, but the full chain is available for consumers that need it.

**Changes:**
- Add `conditionChain` optional field to `ModuleInfo` (TypeScript and C#)
- Populate `conditionChain` alongside `condition`
- `normalizeCondition` still selects the primary, but no longer discards the full chain

**Tests affected:** Condition assignment — verify chain is preserved.

---

## Summary Table

| Phase | Change | Risk | Tests Affected |
|-------|--------|------|----------------|
| 1 | Introduce `ExtractionContext`, remove globals | Low | None — same behavior |
| 2 | Replace `simplifyType` with `displayType` using `TypeFormatFlags` | Low | Type display assertions |
| 3 | Remove `collectRawTypeName`, rely on `walkType` + import fallback only | Medium | Dependency tracking for uninstalled packages |
| 4 | Replace `any` casts with type guards | Low | None |
| 5 | Replace `_` filter with language-level visibility | Low | Some types newly included |
| 6 | Replace string-tokenized reachability with symbol-graph reachability | Medium | Reachability edge cases |
| 7 | Replace path-mapping heuristics with compiler module resolution | Medium | Entry point detection |
| 8 | Add structured diagnostics | Low | New diagnostic output |
| 9 | Lazy dependency file loading | Low | Performance improvement |
| 10 | Add overloads, index signatures, accessors | Low (additive) | New model fields |
| 11 | Condition normalization — preserve full chain | Low | Condition assignment |

## Notes

- Phases 1-4 are pure refactors and can be done first with confidence.
- Phase 3 has the most risk: the dual-tracking exists because ts-morph can't resolve types from uninstalled packages. The fallback is import declarations — the compiler already parsed `import { Foo } from "pkg"` and gives us the symbol `Foo` even if its type is `any`. Existing `collectFromImportDeclarations` handles this.
- Phase 5 is a semantic change — previously hidden `_`-prefixed members will appear. May need a deprecation period or config flag.
- Phases 6 and 7 change the resolution strategy. Run the full test suite against real SDKs to validate.
- Phase 10 is purely additive — new model fields with no existing field changes.
- The `__namespace_reexport__` marker in `extractExportedSymbols` is dead code and should be deleted in Phase 1 or 2.
