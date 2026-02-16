import type { HttpPolicy, HttpRequest, HttpResponse } from "some-http-lib";
/** Options for configuring the client. */
export interface ClientOptions {
    /** The service endpoint. */
    endpoint: string;
    /** Request timeout in milliseconds. */
    timeout?: number;
    /** Custom HTTP policies. */
    policies?: HttpPolicy[];
}
/** A resource returned by the service. */
export interface Resource {
    /** The resource ID. */
    id: string;
    /** The resource name. */
    name: string;
    /** Creation timestamp. */
    createdAt: Date;
}
/** Base client with shared functionality. */
export declare abstract class BaseClient {
    protected readonly endpoint: string;
    constructor(options: ClientOptions);
    /** Lists resources. */
    abstract listResources(): Promise<Resource[]>;
    /** Sends a raw HTTP request. */
    abstract sendRequest(request: HttpRequest): Promise<HttpResponse>;
}
