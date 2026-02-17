import { ClientOptions, Resource, BaseClient } from "../shared";
import type { HttpRequest, HttpResponse } from "some-http-lib";
/** Browser-specific options. */
export interface BrowserClientOptions extends ClientOptions {
    /** Whether to use fetch AbortController (browser-only). */
    useAbortController?: boolean;
    /** Custom fetch implementation (browser-only). */
    fetchImpl?: typeof fetch;
}
/** Client optimized for browser environments. */
export declare class BrowserClient extends BaseClient {
    constructor(options: BrowserClientOptions);
    /** Lists resources using browser fetch API. */
    listResources(): Promise<Resource[]>;
    /** Opens a resource in a new browser tab — browser-only API. */
    openInNewTab(resourceId: string): void;
    /** Downloads a resource as a Blob — browser-only API. */
    downloadAsBlob(resourceId: string): Promise<Blob>;
    /** Sends a raw HTTP request. */
    sendRequest(request: HttpRequest): Promise<HttpResponse>;
}
export { ClientOptions, Resource, BaseClient } from "../shared";
