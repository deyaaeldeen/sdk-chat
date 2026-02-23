// Non-standard layout fixture: types live in build/typings/ instead of dist/types/.
// Tests Phase 7 of the redesign plan — the engine should not rely on hardcoded
// dist/→src/ path mappings but instead use TypeScript's own module resolution.

/** Client with non-standard directory layout */
export declare class LayoutClient {
    /** Initialize the client connection */
    initialize(): Promise<void>;
    /** Get current status */
    getStatus(): string;
    /** Shutdown the client */
    close(): void;
}

/** Configuration options for LayoutClient */
export interface LayoutOptions {
    /** Service endpoint URL */
    endpoint: string;
    /** Request timeout in milliseconds */
    timeout?: number;
    /** Enable debug logging */
    debug?: boolean;
}

/** Status response from the service */
export interface LayoutStatus {
    /** Whether the service is healthy */
    healthy: boolean;
    /** Service uptime in seconds */
    uptime: number;
}
