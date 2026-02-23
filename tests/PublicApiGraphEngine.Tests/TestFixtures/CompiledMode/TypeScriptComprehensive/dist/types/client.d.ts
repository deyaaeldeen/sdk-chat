import type { PipelinePolicy, PipelineRequest, PipelineResponse } from "@azure/core-rest-pipeline";
import type { Logger } from "some-logger";
import type { Resource, PagedResult, OperationResult, ResourceState } from "./models/index";

/** Options for retry behavior. */
export interface RetryOptions {
    /** Maximum number of retries. */
    maxRetries: number;
    /** Base delay in milliseconds between retries. */
    retryDelayInMs: number;
    /** Maximum delay in milliseconds between retries. */
    maxRetryDelayInMs: number;
}

/** Options for configuring the comprehensive client. */
export interface ClientOptions {
    /** The service endpoint URL. */
    endpoint: string;
    /** Additional pipeline policies. */
    additionalPolicies?: PipelinePolicy[];
    /** Retry configuration. */
    retryOptions?: RetryOptions;
    /** Logger instance. */
    logger?: Logger;
    /** Request timeout in milliseconds. */
    timeout?: number;
    /** API version to use. */
    apiVersion?: string;
}

/**
 * A comprehensive client demonstrating all TypeScript patterns.
 *
 * Exercises: overloads, accessors, generics, abstract methods,
 * underscore-prefixed public members, ECMAScript private fields.
 */
export declare class ComprehensiveClient {
    /** The service endpoint. */
    readonly endpoint: string;

    /**
     * Creates a new ComprehensiveClient.
     * @param endpoint - The service endpoint URL.
     * @param options - Client configuration options.
     */
    constructor(endpoint: string, options?: ClientOptions);

    // --- Method Overloads ---

    /**
     * Gets a resource by ID (string overload).
     * @param resourceId - The resource ID.
     */
    getResource(resourceId: string): Promise<Resource>;
    /**
     * Gets a resource by ID with options (object overload).
     * @param resourceId - The resource ID.
     * @param options - Request options.
     */
    getResource(resourceId: string, options: { includeDeleted?: boolean; expand?: string[] }): Promise<Resource>;

    /**
     * Lists resources with optional filter.
     * @param filter - OData filter string.
     */
    listResources(filter?: string): AsyncIterableIterator<PagedResult<Resource>>;

    /**
     * Creates a resource.
     * @param resource - The resource to create.
     */
    createResource(resource: Omit<Resource, "id" | "createdAt" | "state">): Promise<OperationResult<Resource>>;

    /**
     * Deletes a resource (void overload).
     * @param resourceId - The resource to delete.
     */
    deleteResource(resourceId: string): Promise<void>;
    /**
     * Deletes a resource (boolean overload).
     * @param resourceId - The resource to delete.
     * @param options - Delete options.
     */
    deleteResource(resourceId: string, options: { soft?: boolean }): Promise<boolean>;

    /**
     * Sends a raw pipeline request.
     * @param request - The pipeline request.
     */
    sendRequest(request: PipelineRequest): Promise<PipelineResponse>;

    // --- Get/Set Accessors ---

    /** Gets the current API version. */
    get apiVersion(): string;

    /** Gets or sets the request timeout. */
    get requestTimeout(): number;
    set requestTimeout(value: number);

    // --- Underscore-prefixed public method (NOT private — should be visible) ---

    /**
     * Serializes a resource for wire format.
     * This method starts with underscore but is explicitly public.
     * The engine should NOT filter it out based on naming convention.
     */
    _serializeResource(resource: Resource): string;

    /**
     * Transforms raw response data.
     * Another underscore-prefixed public method.
     */
    _transformResponse<T>(raw: unknown, deserializer: (data: unknown) => T): T;

    // --- ECMAScript private fields (SHOULD be excluded) ---
    // Note: #privateField syntax can't appear in .d.ts,
    // but private keyword marks them in declarations
    private _internalState: unknown;
    private _logger: Logger | undefined;

    // --- Static methods ---

    /**
     * Creates a client from a connection string.
     * @param connectionString - The connection string.
     */
    static fromConnectionString(connectionString: string, options?: ClientOptions): ComprehensiveClient;
}

