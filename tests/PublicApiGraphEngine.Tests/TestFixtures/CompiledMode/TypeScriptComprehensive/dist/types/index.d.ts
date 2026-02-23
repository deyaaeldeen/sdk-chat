/**
 * Comprehensive test fixture for TypeScript compiled-mode engine.
 *
 * This fixture exercises every TypeScript pattern the engine must handle:
 * - Method overloads
 * - Index signatures
 * - Get/set accessors
 * - Generic types with constraints
 * - Conditional types
 * - Mapped types
 * - Union and intersection types
 * - Abstract classes and methods
 * - Underscore-prefixed public members (not private — should be included)
 * - ECMAScript private fields (#field — should be excluded)
 * - JSDoc @internal / @deprecated annotations
 * - Type aliases (simple and complex)
 * - Const enums
 * - Re-exports from external packages
 * - Nested generic types
 * - Optional and rest parameters
 * - Default parameter values
 * - Readonly properties
 * - Import types from uninstalled packages
 */

// Re-export types from external packages
export type { PipelinePolicy, PipelineRequest, PipelineResponse } from "@azure/core-rest-pipeline";
export type { Logger } from "some-logger";

// Core types
export { ComprehensiveClient } from "./client";
export type { ClientOptions, RetryOptions } from "./client";
export { AdvancedTypes } from "./advanced-types";
export type {
    Resource,
    PagedResult,
    OperationResult,
    ResourceState,
    DeepPartial,
    KeysOfType,
    EventMap,
    EventHandler,
    StringMap,
    ReadonlyResource,
    ResourceOrError,
    NestedGeneric,
} from "./models/index";
export { ResultStatus, LogLevel } from "./models/index";
export type { StreamingClient, StreamEvent, StreamOptions } from "./streaming";

// Underscore-prefixed public exports (NOT private — should be visible)
export { _Serializer, _InternalHelper } from "./advanced-types";

