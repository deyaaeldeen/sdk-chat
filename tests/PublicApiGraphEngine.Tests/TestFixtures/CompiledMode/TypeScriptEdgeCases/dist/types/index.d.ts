// Edge case declarations for testing TypeScript engine redesign phases.
// These types exercise patterns that the current engine handles incorrectly
// or incompletely, validating that the redesign plan addresses them.

// Re-export submodule types so they appear in the API
export { SubmoduleClient, SubmoduleConfig } from "./submodule";

// ============================================================================
// Phase 2: typeof import() — simplifyType regex cannot handle this
// ============================================================================
//
// The simplifyType regex /import\([^)]+\)\./g requires a dot after the closing
// paren. "typeof import('./submodule')" has no dot, so the regex doesn't match
// and the raw import() syntax leaks into the type display.

/** Returns the submodule namespace object */
export declare function getSubmoduleNamespace(): typeof import("./submodule");

// ============================================================================
// Phase 6: Types reachable only through complex type resolution
// ============================================================================

// Indexed access type: ResolvedDBConfig = AppSettings["database"]
// String BFS tokenizes "AppSettings['database']" and finds "AppSettings" but
// cannot resolve the indexed access to determine that DatabaseSettings (the
// concrete type) is semantically reachable.

export interface AppSettings {
    database: DatabaseSettings;
    logging: LoggingSettings;
}

export interface DatabaseSettings {
    host: string;
    port: number;
    pool: PoolSettings;
}

export interface PoolSettings {
    maxConnections: number;
    idleTimeout: number;
}

export interface LoggingSettings {
    level: string;
    format: string;
}

// This type alias evaluates to DatabaseSettings but its text representation
// is "AppSettings['database']" which string tokenization can't follow.
export type ResolvedDBConfig = AppSettings["database"];

// Conditional type — both branches contain distinct types.
// String BFS can find these in the type text, but compiler-driven reachability
// would also follow through infer/conditional expansion.
export type SafeResult<T> = T extends Error ? ErrorDetail : SuccessDetail<T>;

export interface ErrorDetail {
    code: string;
    message: string;
    stack?: string;
}

export interface SuccessDetail<T> {
    data: T;
    timestamp: number;
}

// Entry point class that uses the edge case types
export declare class EdgeCaseClient {
    /** Returns database configuration resolved from settings */
    getDBConfig(): ResolvedDBConfig;
    /** Wraps result in a type-safe discriminated result */
    processResult<T>(input: T): SafeResult<T>;
    /** Returns the submodule namespace (typeof import) */
    getSubmoduleNamespace(): typeof import("./submodule");
}
