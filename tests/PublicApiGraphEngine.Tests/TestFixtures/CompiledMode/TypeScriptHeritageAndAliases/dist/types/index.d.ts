// Test fixture for heritage clause namespace stripping and type alias resolution.
//
// Exercises fixes:
//
// Fix 1: Namespace alias tracking to strip import prefixes from heritage clauses.
//         When a class "extends CoreModels.BaseClient", the engine should resolve
//         this to "BaseClient" and find it in the @dep/core dependency.
//
// Fix 2: collectFromTypeNode calls in extractTypeAlias, extractInterface, extractClass.
//         Type aliases with complex bodies and interfaces with extends clauses
//         should have all referenced types discovered.
//
// Fix 3: ExpressionWithTypeArguments handler with namespace-qualified name filter.
//         The "CoreModels.BaseClient" in extends should NOT be treated as a type
//         named "CoreModels.BaseClient" but resolved to "BaseClient".
//
// Fix 4: Type alias bodies should have references extracted via collectFromTypeNode
//         so that dependency types in alias bodies are discovered.

// ============================================================================
// Namespace import — the alias "CoreModels" is NOT a type; it's a module object.
// ============================================================================
import * as CoreModels from "@dep/core";

// Named import from the same package (for contrast)
import { TokenCredential } from "@dep/core";

// ============================================================================
// Heritage clause with namespace-qualified name (Fix 1, Fix 3)
// ============================================================================

/** Options that extend a dependency type through namespace import */
export interface StorageOptions extends CoreModels.BaseOptions {
    /** Storage account name */
    accountName: string;
    /** Authentication configuration via namespace import */
    auth: CoreModels.AuthConfig;
}

/** Client that extends a dependency interface through namespace import */
export declare class StorageClient implements CoreModels.BaseClient {
    readonly endpoint: string;
    constructor(options: StorageOptions);
    close(): Promise<void>;
    getServiceVersion(): CoreModels.ServiceVersion;
}

// ============================================================================
// Type aliases with complex bodies referencing dependency types (Fix 2, Fix 4)
// ============================================================================

/** Intersection type alias that references dependency types */
export type ExtendedOptions = CoreModels.BaseOptions & {
    /** Extra property specific to this package */
    extra?: string;
    /** Authentication config from dependency */
    auth?: CoreModels.AuthConfig;
};

/** Union type alias with dependency types */
export type CredentialInput = TokenCredential | CoreModels.AuthConfig | string;

/** Conditional type alias referencing dependency types */
export type ResolvedAuth<T> = T extends CoreModels.AuthConfig ? T : CoreModels.AuthConfig;

// ============================================================================
// Class using named imports in heritage (baseline — should work without fix)
// ============================================================================

/** Helper that uses named import in constructor parameter */
export declare class AuthHelper {
    constructor(credential: TokenCredential);
    authenticate(): Promise<CoreModels.AuthConfig>;
}
