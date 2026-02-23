import type { Resource, PagedResult } from "./models/index";

/** Options for streaming operations. */
export interface StreamOptions {
    /** Maximum number of events to buffer. */
    bufferSize?: number;
    /** Timeout for individual events in milliseconds. */
    eventTimeoutMs?: number;
    /** Whether to automatically reconnect on failure. */
    autoReconnect?: boolean;
}

/** A streaming event with generic payload. */
export interface StreamEvent<T = unknown> {
    /** Event ID. */
    id: string;
    /** Event type discriminator. */
    type: "data" | "error" | "complete" | "heartbeat";
    /** Event payload — only present for "data" type. */
    data?: T;
    /** Error details — only present for "error" type. */
    error?: Error;
    /** Server timestamp. */
    timestamp: Date;
}

/**
 * Streaming client with method overloads and generic methods.
 */
export declare class StreamingClient {
    /**
     * Creates a new StreamingClient.
     * @param endpoint - The service endpoint.
     * @param options - Streaming options.
     */
    constructor(endpoint: string, options?: StreamOptions);

    // --- Method overloads with generic type parameters ---

    /**
     * Streams resources (no type parameter — defaults to Resource).
     */
    stream(): AsyncIterableIterator<StreamEvent<Resource>>;
    /**
     * Streams typed events.
     * @param eventType - The event type to filter.
     */
    stream<T>(eventType: string): AsyncIterableIterator<StreamEvent<T>>;

    /**
     * Gets paged results via streaming.
     */
    streamPages(): AsyncIterableIterator<PagedResult<Resource>>;

    /**
     * Closes the stream connection.
     */
    close(): Promise<void>;

    /** Whether the client is currently connected. */
    get isConnected(): boolean;
}

