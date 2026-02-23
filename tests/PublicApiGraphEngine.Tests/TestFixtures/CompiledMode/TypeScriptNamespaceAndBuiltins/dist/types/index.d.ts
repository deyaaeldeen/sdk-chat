// Test fixture for namespace import and Node built-in handling.
//
// Exercises three bugs fixed in commit 86e64a0:
//
// Bug 1: Namespace imports (import * as X) were not handled by
//         buildImportResolutionMap, causing namespace aliases to appear
//         as unresolved dependency types.
//
// Bug 2: Namespace imports are module aliases, not types. Even when
//         tracked in importedTypes, they can't be looked up as exported
//         declarations. The compiler resolves `extLib.INetworkModule`
//         to the actual `INetworkModule` type automatically.
//
// Bug 3: Node built-in modules (node:child_process, events, fs, etc.)
//         have no installable type packages. The engine should suppress
//         them from unresolved type stubs and diagnostics.

// ============================================================================
// Namespace imports (Bug 1 & 2)
// ============================================================================

// Namespace import — the alias "extLib" is NOT a type; it's a module object.
// Types accessed via `extLib.INetworkModule` should resolve to `INetworkModule`.
import * as extLib from "ext-lib";

// Named import from the same package (for contrast)
import { ExtLogger } from "ext-lib";

// ============================================================================
// Node built-in imports (Bug 3)
// ============================================================================

// node: prefix form
import { ChildProcess } from "node:child_process";

// Bare module name form (also a Node built-in)
import { EventEmitter } from "events";

// Another node: prefix form
import { Readable } from "node:stream";

// ============================================================================
// Exported API that references the above imports
// ============================================================================

/** Options for configuring the namespace client. */
export interface NamespaceClientOptions {
    /** Network configuration accessed via namespace import */
    network: extLib.INetworkModule;
    /** Auth configuration accessed via namespace import */
    auth: extLib.AuthConfig;
    /** Logger (named import, not namespace) */
    logger?: ExtLogger;
    /** Request timeout in milliseconds */
    timeout?: number;
}

/** Result of a token acquisition */
export interface TokenResult {
    /** The cached token record accessed via namespace import */
    cache: extLib.TokenCacheRecord;
    /** When the token was acquired */
    acquiredAt: Date;
}

/**
 * Client class that uses namespace imports and Node built-in types.
 * Tests that:
 * - `extLib.INetworkModule` resolves to `INetworkModule` (not `extLib = unknown`)
 * - `ChildProcess`, `EventEmitter`, `Readable` from Node built-ins
 *   don't create unresolved type stubs
 */
export declare class NamespaceClient {
    /** Creates a new client with namespace-imported options */
    constructor(options: NamespaceClientOptions);

    /** Returns a child process (Node built-in type) */
    spawn(command: string): ChildProcess;

    /** Returns an event emitter (Node built-in, bare import) */
    getEmitter(): EventEmitter;

    /** Returns a readable stream (Node built-in, node: prefix) */
    getStream(): Readable;

    /** Acquires a token using namespace-imported types */
    acquireToken(): Promise<TokenResult>;

    /** Gets the network config via namespace import */
    getNetworkConfig(): extLib.INetworkModule;
}

/** Standalone function using namespace import in return type */
export declare function createAuthConfig(clientId: string, tenantId: string): extLib.AuthConfig;

/** Standalone function using Node built-in in parameter type */
export declare function processStream(input: Readable): Promise<string>;
