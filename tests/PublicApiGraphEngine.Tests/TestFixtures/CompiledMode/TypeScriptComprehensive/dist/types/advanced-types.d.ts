/**
 * Advanced type patterns that challenge the engine.
 *
 * - Underscore-prefixed public class (should be visible)
 * - Abstract base class
 * - Intersection types
 * - Class with only accessors (no regular properties or methods)
 * - @deprecated members
 * - @internal members (should be excluded)
 */

import type { Resource } from "./models/index";

/**
 * Public serializer class with underscore prefix.
 * The engine should include this — it's a public export, not a private member.
 */
export declare class _Serializer {
    /**
     * Serializes a resource to JSON string.
     */
    serialize(resource: Resource): string;

    /**
     * Deserializes a JSON string to a resource.
     */
    deserialize(json: string): Resource;
}

/**
 * Another underscore-prefixed public helper.
 */
export declare class _InternalHelper {
    /**
     * Validates a resource ID format.
     */
    static validateId(id: string): boolean;
}

/**
 * Abstract base with generic type parameter and constraint.
 */
export declare abstract class AdvancedTypes<T extends Record<string, unknown> = Record<string, unknown>> {
    /** The underlying data store. */
    protected readonly store: Map<string, T>;

    constructor();

    /** Gets a value by key. */
    abstract get(key: string): T | undefined;

    /** Sets a value by key. */
    abstract set(key: string, value: T): void;

    /**
     * Merges two values using intersection type.
     */
    merge<A extends T, B extends T>(a: A, b: B): A & B;

    /**
     * @deprecated Use `get` instead. Will be removed in v3.0.
     */
    lookup(key: string): T | undefined;

    /**
     * @internal
     * Internal-only method — should not appear in public API.
     */
    _resetStore(): void;
}

