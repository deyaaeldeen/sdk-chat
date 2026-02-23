/**
 * Models exercising advanced TypeScript type patterns.
 *
 * - Generics with constraints
 * - Union types (discriminated and simple)
 * - Intersection types
 * - Mapped types
 * - Conditional types
 * - Index signatures
 * - Readonly properties
 * - Nested generics
 * - Const enums
 * - Tuple types
 */

/** Status of a resource in the service. */
export declare const enum ResourceState {
    Active = "active",
    Inactive = "inactive",
    Deleted = "deleted",
    Provisioning = "provisioning",
}

/** Result status for operations. */
export declare enum ResultStatus {
    Success = "success",
    Failed = "failed",
    Pending = "pending",
    Cancelled = "cancelled",
}

/** Log level for the logger. */
export declare enum LogLevel {
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}

/** A resource returned by the service. */
export interface Resource {
    /** The resource ID. */
    readonly id: string;
    /** The resource name. */
    name: string;
    /** Resource state. */
    state: ResourceState;
    /** Optional tags. */
    tags?: Record<string, string>;
    /** Creation timestamp. */
    readonly createdAt: Date;
    /** Last update timestamp. */
    updatedAt?: Date;
    /** Optional metadata — index signature pattern. */
    [key: string]: unknown;
}

/** Paged result with generic type parameter. */
export interface PagedResult<T> {
    /** The items in this page. */
    items: T[];
    /** Continuation token for the next page. */
    continuationToken?: string;
    /** Total count if available. */
    totalCount?: number;
}

/** Operation result — discriminated union. */
export type OperationResult<T> =
    | { status: "succeeded"; value: T; etag: string }
    | { status: "failed"; error: Error; retryAfterMs?: number }
    | { status: "cancelled"; reason?: string };

/** Deep partial — mapped conditional type. */
export type DeepPartial<T> = {
    [P in keyof T]?: T[P] extends object ? DeepPartial<T[P]> : T[P];
};

/** Extract keys whose values match a type — conditional + mapped type. */
export type KeysOfType<T, V> = {
    [K in keyof T]: T[K] extends V ? K : never;
}[keyof T];

/** Event map — index signature with function type values. */
export interface EventMap {
    [eventName: string]: (...args: unknown[]) => void;
}

/** Type-safe event handler using conditional types. */
export type EventHandler<TEvent extends keyof EventMap> =
    EventMap[TEvent] extends (...args: infer P) => void ? (...args: P) => void : never;

/** Simple string-keyed map — index signature. */
export interface StringMap {
    [key: string]: string;
}

/** Readonly version of Resource using mapped type. */
export type ReadonlyResource = Readonly<Resource>;

/** Union type combining Resource or Error. */
export type ResourceOrError = Resource | Error;

/** Nested generic type. */
export type NestedGeneric<T> = PagedResult<OperationResult<T>>;

