// Simple submodule used as a typeof import() target.
// This module is referenced by index.d.ts via typeof import("./submodule")
// to test Phase 2 (import prefix cleaning).

export declare class SubmoduleClient {
    doWork(): Promise<void>;
    getStatus(): string;
}

export interface SubmoduleConfig {
    enabled: boolean;
    maxRetries: number;
}
