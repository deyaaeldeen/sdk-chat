# Proposal: Replace TypeScript Engine with SWC-based Rust Implementation

**Status:** Draft  
**Author:** AI Investigation  
**Date:** 2026-02-02  

## Summary

Replace the current Bun-compiled TypeScript engine (97 MB) with a native Rust binary using SWC parser (1.2 MB), reducing release container size by ~96 MB (19% of total).

## Current State

The release container (`sdk-chat:latest`) is 504 MB, with the following breakdown:

| Component | Size | Percentage |
|-----------|------|------------|
| Copilot CLI | 138 MB | 27% |
| **ts_engine** | **97 MB** | **19%** |
| sdk-chat (main CLI) | 40.5 MB | 8% |
| java_engine | 36.4 MB | 7% |
| python_engine | 8.1 MB | 2% |
| go_engine | 2.6 MB | 0.5% |
| Base image | ~22 MB | 4% |

The TypeScript engine is the **second-largest component** because it bundles the entire Bun runtime + ts-morph + TypeScript compiler.

## Proposed Solution

Replace the ts-morph/Bun implementation with a native Rust binary using [SWC](https://swc.rs/) (Speedy Web Compiler) for TypeScript parsing.

### Proof of Concept Results

A minimal SWC-based engine was built and tested:

```
Current (Bun):    97 MB
SWC (Rust):      1.2 MB
─────────────────────────
Savings:        95.8 MB (98.8% reduction)
```

### Why SWC?

1. **Native Rust** - Compiles to a single static binary
2. **Battle-tested** - Used by Next.js, Deno, Turbopack
3. **TypeScript-native** - Full TypeScript/TSX parsing support
4. **Active development** - Maintained by Vercel

## Technical Analysis

### What SWC Can Do

- ✅ Parse TypeScript/TSX syntax
- ✅ Extract class, interface, function, enum declarations
- ✅ Get method signatures and property types
- ✅ Read JSDoc comments from AST nodes
- ✅ Handle decorators and generics

### What SWC Cannot Do (Without Extra Work)

- ❌ Resolve type aliases across files (no type checker)
- ❌ Infer generic type parameters at call sites
- ❌ Follow imports to get full type information
- ❌ Provide semantic analysis like ts-morph

### Feature Parity Assessment

| Feature | ts-morph | SWC | Impact |
|---------|----------|-----|--------|
| Parse TypeScript | ✅ | ✅ | None |
| Extract classes/interfaces | ✅ | ✅ | None |
| Get method signatures | ✅ | ✅ | None |
| Read JSDoc comments | ✅ | ✅ | None |
| Resolve type aliases | ✅ | ❌ | Medium - types shown as-is |
| Cross-file type resolution | ✅ | ❌ | Low - most APIs are self-contained |
| Generic type inference | ✅ | ❌ | Low - generics preserved in source |

**Estimated feature coverage: 80-90%** for typical SDK engine needs.

## Implementation Plan

### Phase 1: Core Parser (1-2 days)

Create a new Rust crate `public-api-graph-engine-ts-rust/` with:

1. CLI argument parsing (clap)
2. TypeScript file discovery (walkdir)
3. SWC parsing integration
4. AST visitor for graphing:
   - Exported classes with methods/properties
   - Exported interfaces
   - Exported functions
   - Exported enums and type aliases

### Phase 2: Output Compatibility (0.5 days)

Match the existing JSON output format:

```json
{
  "package": "openai",
  "modules": [
    {
      "name": "client",
      "classes": [
        {
          "name": "OpenAI",
          "methods": [{ "name": "chat", "sig": "options: ChatOptions", "ret": "Promise<Response>" }]
        }
      ]
    }
  ]
}
```

### Phase 3: Integration (0.5 days)

1. Add Rust build stage to `engines/typescript/Dockerfile`
2. Update `TypeScriptPublicApiGraphEngine.cs` to use new binary
3. Run existing tests to verify compatibility

### Phase 4: Fallback Strategy (Optional)

Keep Bun version as fallback for edge cases:
- Environment variable to choose implementation
- Automatic fallback if Rust engine fails

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| Missing type information | Medium | Low | Types preserved as source text |
| SWC API breaking changes | Low | Medium | Pin to specific version |
| Complex generic types | Medium | Low | Preserve generic syntax as-is |
| JSDoc parsing differences | Low | Low | Test against real SDKs |

## Build Dependencies

Add to `engines/typescript/Dockerfile`:

```dockerfile
# Stage: Build TypeScript engine (Rust/SWC)
FROM rust:1.75-bookworm AS build-typescript

WORKDIR /src
COPY src/public-api-graph-engine-ts-rust/ .

RUN cargo build --release \
    --config 'profile.release.opt-level="z"' \
    --config 'profile.release.lto=true' \
    --config 'profile.release.strip=true'
```

## Cargo.toml Dependencies

```toml
[dependencies]
swc_ecma_parser = "0.149"
swc_common = "0.37"
serde = { version = "=1.0.203", features = ["derive"] }
serde_json = "1"
clap = { version = "4", features = ["derive"] }
walkdir = "2"
```

Note: Pin `serde = "=1.0.203"` for SWC compatibility.

## Success Metrics

- [ ] Binary size < 5 MB (vs current 97 MB)
- [ ] Passes existing TypeScript engine tests
- [ ] No regression in sample generation quality
- [ ] Build time < 2 minutes in CI

## Container Size Impact

| Before | After | Savings |
|--------|-------|---------|
| 504 MB | ~410 MB | ~94 MB (19%) |

## Alternatives Considered

### Node.js Single Executable Application (SEA)

- **Pros:** Keep existing code, Node.js 20+ native support
- **Cons:** Still ~80-90 MB, requires bundling Node.js runtime
- **Verdict:** Marginal improvement, not worth the effort

### Deno Compile

- **Pros:** Smaller than Bun, better TypeScript support
- **Cons:** ts-morph may not work, still bundles V8 (~70-90 MB)
- **Verdict:** Uncertain compatibility, similar size

### Keep Current (Bun)

- **Pros:** No work required, full feature parity
- **Cons:** 97 MB binary, largest engine by far
- **Verdict:** Acceptable if size is not a priority

## Recommendation

Proceed with SWC-based Rust implementation. The 19% container size reduction is significant, and the 80-90% feature coverage is sufficient for SDK API graphing. The fallback strategy mitigates risk.

**Estimated effort:** 2-3 developer-days

## References

- [SWC Documentation](https://swc.rs/)
- [swc_ecma_parser crate](https://crates.io/crates/swc_ecma_parser)
- [Current TypeScript engine](../src/PublicApiGraphEngine.TypeScript/src/graph_api.ts)
