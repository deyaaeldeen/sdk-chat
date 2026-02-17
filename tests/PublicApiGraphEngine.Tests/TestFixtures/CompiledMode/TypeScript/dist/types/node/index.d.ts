import { ClientOptions, Resource, BaseClient } from "../shared";
import type { HttpRequest, HttpResponse } from "some-http-lib";
/** Node-specific options with filesystem support. */
export interface NodeClientOptions extends ClientOptions {
    /** Path to the certificate file (node-only). */
    certPath?: string;
    /** Whether to use HTTP/2 (node-only). */
    useHttp2?: boolean;
}
/** Client optimized for Node.js with streaming and filesystem support. */
export declare class NodeClient extends BaseClient {
    private certPath?;
    constructor(options: NodeClientOptions);
    /** Lists resources using Node.js HTTP client. */
    listResources(): Promise<Resource[]>;
    /** Streams a resource to a file path — node-only API. */
    streamToFile(resourceId: string, filePath: string): Promise<void>;
    /** Reads a resource from a file — node-only API. */
    readFromFile(filePath: string): Promise<Resource>;
    /** Sends a raw HTTP request. */
    sendRequest(request: HttpRequest): Promise<HttpResponse>;
}
export { ClientOptions, Resource, BaseClient } from "../shared";
