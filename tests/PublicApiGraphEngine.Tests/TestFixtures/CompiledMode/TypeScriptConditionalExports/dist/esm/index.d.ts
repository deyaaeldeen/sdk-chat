// ESM entry point for conditional exports fixture.
// Tests Phase 11 of the redesign plan — the engine should preserve the
// "import" condition from package.json exports on the ModuleInfo.

/** Client for the conditional exports package (ESM) */
export declare class ConditionalClient {
    /** Connect to the service */
    connect(): Promise<void>;
    /** Disconnect from the service */
    disconnect(): void;
    /** Check if currently connected */
    isConnected(): boolean;
}

/** Options for ConditionalClient */
export interface ConditionalOptions {
    /** Service URL */
    url: string;
    /** Protocol to use */
    protocol?: string;
}
