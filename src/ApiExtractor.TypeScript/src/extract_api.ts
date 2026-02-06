#!/usr/bin/env node
/**
 * Extract public API surface from TypeScript/JavaScript packages.
 * Uses ts-morph for proper TypeScript parsing.
 */

import {
    Project,
    SourceFile,
    ClassDeclaration,
    InterfaceDeclaration,
    FunctionDeclaration,
    TypeAliasDeclaration,
    EnumDeclaration,
    MethodDeclaration,
    PropertyDeclaration,
    ConstructorDeclaration,
    ParameterDeclaration,
    JSDocableNode,
    Node,
    Type,
    Symbol as TsSymbol,
    ts,
} from "ts-morph";
import * as fs from "fs";
import * as path from "path";

// ============================================================================
// API Models - Strongly Typed
// ============================================================================

export interface MethodInfo {
    name: string;
    sig: string;
    ret?: string;
    doc?: string;
    async?: boolean;
    static?: boolean;
}

export interface PropertyInfo {
    name: string;
    type: string;
    readonly?: boolean;
    optional?: boolean;
    doc?: string;
}

export interface ConstructorInfo {
    sig: string;
}

export interface ClassInfo {
    name: string;
    entryPoint?: boolean;
    exportPath?: string;  // The subpath to import from (e.g., "." or "./client")
    reExportedFrom?: string;  // External package this is re-exported from (e.g., "@azure/core-client")
    extends?: string;
    implements?: string[];
    typeParams?: string;
    doc?: string;
    constructors?: ConstructorInfo[];
    methods?: MethodInfo[];
    properties?: PropertyInfo[];
}

export interface InterfaceInfo {
    name: string;
    entryPoint?: boolean;
    exportPath?: string;  // The subpath to import from (e.g., "." or "./client")
    reExportedFrom?: string;  // External package this is re-exported from
    extends?: string;
    typeParams?: string;
    doc?: string;
    methods?: MethodInfo[];
    properties?: PropertyInfo[];
}

export interface EnumInfo {
    name: string;
    reExportedFrom?: string;  // External package this is re-exported from
    doc?: string;
    values: string[];
}

export interface TypeAliasInfo {
    name: string;
    type: string;
    reExportedFrom?: string;  // External package this is re-exported from
    doc?: string;
}

export interface FunctionInfo {
    name: string;
    entryPoint?: boolean;
    exportPath?: string;  // The subpath to import from (e.g., "." or "./client")
    reExportedFrom?: string;  // External package this is re-exported from
    sig: string;
    ret?: string;
    doc?: string;
    async?: boolean;
}

export interface ModuleInfo {
    name: string;
    /** If this module is from a dependency, the package name */
    fromPackage?: string;
    classes?: ClassInfo[];
    interfaces?: InterfaceInfo[];
    enums?: EnumInfo[];
    types?: TypeAliasInfo[];
    functions?: FunctionInfo[];
}

export interface ApiIndex {
    package: string;
    modules: ModuleInfo[];
    /** Types from dependency packages that appear in the public API */
    dependencies?: DependencyInfo[];
}

/**
 * Information about types from a dependency package.
 */
export interface DependencyInfo {
    /** The npm package name */
    package: string;
    /** Types from this package that are referenced in the API */
    classes?: ClassInfo[];
    interfaces?: InterfaceInfo[];
    enums?: EnumInfo[];
    types?: TypeAliasInfo[];
}

// ============================================================================
// Builtin Type Detection
// ============================================================================

/**
 * Paths that indicate a type is a builtin (from TypeScript lib or @types/node).
 */
const BUILTIN_PATH_PATTERNS = [
    "/typescript/lib/",      // TypeScript lib files (lib.dom.d.ts, lib.es*.d.ts)
    "/@types/node/",         // Node.js types
    "/node_modules/typescript/", // Alternative path format
];

/**
 * Well-known builtin type names that are part of TypeScript/JavaScript/DOM.
 * This is a fallback when type resolution isn't possible.
 * Generated from TypeScript lib files - covers ES5 through ESNext and DOM.
 */
const WELL_KNOWN_BUILTINS = new Set([
    // ES fundamentals
    "Array", "ArrayBuffer", "ArrayBufferConstructor", "ArrayBufferLike", "ArrayBufferTypes",
    "ArrayConstructor", "ArrayLike", "AsyncGenerator", "AsyncGeneratorFunction", "AsyncGeneratorFunctionConstructor",
    "AsyncIterable", "AsyncIterableIterator", "AsyncIterator", "Atomics",
    "BigInt", "BigInt64Array", "BigInt64ArrayConstructor", "BigIntConstructor", "BigIntToLocaleStringOptions",
    "BigUint64Array", "BigUint64ArrayConstructor", "Boolean", "BooleanConstructor",
    "CallableFunction", "ConcatArray", "Console", "DataView", "DataViewConstructor", "Date", "DateConstructor",
    "Error", "ErrorConstructor", "EvalError", "EvalErrorConstructor",
    "Float32Array", "Float32ArrayConstructor", "Float64Array", "Float64ArrayConstructor",
    "Function", "FunctionConstructor", "Generator", "GeneratorFunction", "GeneratorFunctionConstructor",
    "IArguments", "ImportMeta", "Int8Array", "Int8ArrayConstructor", "Int16Array", "Int16ArrayConstructor",
    "Int32Array", "Int32ArrayConstructor", "Intl", "IterableIterator", "Iterator", "IteratorResult", "IteratorYieldResult", "IteratorReturnResult",
    "JSON", "Map", "MapConstructor", "Math", "MethodDecorator",
    "NewableFunction", "Number", "NumberConstructor", "Object", "ObjectConstructor",
    "Parameters", "ConstructorParameters", "ReturnType", "InstanceType", "OmitThisParameter", "ThisParameterType", "ThisType",
    "Partial", "Required", "Readonly", "Pick", "Omit", "Record", "Exclude", "Extract", "NonNullable", "Awaited",
    "Uppercase", "Lowercase", "Capitalize", "Uncapitalize", "NoInfer",
    "Promise", "PromiseConstructor", "PromiseLike", "PromiseFulfilledResult", "PromiseRejectedResult", "PromiseSettledResult",
    "PropertyDecorator", "PropertyDescriptor", "PropertyDescriptorMap", "PropertyKey",
    "ProxyHandler", "Proxy", "ProxyConstructor",
    "RangeError", "RangeErrorConstructor", "ReferenceError", "ReferenceErrorConstructor",
    "Reflect", "RegExp", "RegExpConstructor", "RegExpMatchArray", "RegExpExecArray",
    "Set", "SetConstructor", "SharedArrayBuffer", "SharedArrayBufferConstructor",
    "String", "StringConstructor", "Symbol", "SymbolConstructor",
    "SyntaxError", "SyntaxErrorConstructor", "TemplateStringsArray",
    "TypedPropertyDescriptor", "TypeError", "TypeErrorConstructor",
    "Uint8Array", "Uint8ArrayConstructor", "Uint8ClampedArray", "Uint8ClampedArrayConstructor",
    "Uint16Array", "Uint16ArrayConstructor", "Uint32Array", "Uint32ArrayConstructor",
    "URIError", "URIErrorConstructor", "WeakMap", "WeakMapConstructor", "WeakSet", "WeakSetConstructor", "WeakRef", "WeakRefConstructor",
    "FinalizationRegistry", "FinalizationRegistryConstructor",
    // DOM types (commonly used)
    "Blob", "BlobPropertyBag", "File", "FileList", "FileReader", "FileReaderSync",
    "FormData", "FormDataEntryValue", "Headers", "HeadersInit",
    "Request", "RequestInit", "RequestInfo", "Response", "ResponseInit", "ResponseType",
    "URL", "URLSearchParams", "URLSearchParamsInit",
    "ReadableStream", "ReadableStreamDefaultReader", "ReadableStreamBYOBReader",
    "WritableStream", "WritableStreamDefaultWriter", "TransformStream",
    "AbortController", "AbortSignal",
    "EventTarget", "Event", "CustomEvent", "CustomEventInit", "EventInit", "EventListener", "EventListenerOrEventListenerObject",
    "MessageEvent", "MessageEventInit", "MessagePort", "MessageChannel",
    "Worker", "WorkerGlobalScope", "DedicatedWorkerGlobalScope", "SharedWorker", "SharedWorkerGlobalScope",
    "Crypto", "SubtleCrypto", "CryptoKey", "CryptoKeyPair",
    "TextDecoder", "TextEncoder", "TextDecoderStream", "TextEncoderStream",
    "WebSocket", "CloseEvent", "WebSocketEventMap",
    "Storage", "StorageEvent", "IDBDatabase", "IDBTransaction", "IDBObjectStore", "IDBRequest",
    "Document", "Element", "HTMLElement", "Node", "NodeList", "HTMLCollection",
    "Window", "WindowOrWorkerGlobalScope", "Navigator", "Location", "History",
    "Performance", "PerformanceEntry", "PerformanceMark", "PerformanceMeasure",
    "Console", "Timer", "Timeout", "Interval",
    // Node.js built-ins (from @types/node)
    "Buffer", "BufferEncoding", "NodeJS", "EventEmitter",
    "Stream", "Readable", "Writable", "Duplex", "Transform", "PassThrough",
    "ChildProcess", "Cluster", "Worker",
    "IncomingMessage", "ServerResponse", "ClientRequest", "Agent",
    "Socket", "Server", "TLSSocket",
    "Stats", "Dirent", "ReadStream", "WriteStream",
    "Process", "Global",
]);

/**
 * Primitive type names that are always builtins (not resolvable to declarations).
 */
const PRIMITIVE_TYPES = new Set([
    "string", "number", "boolean", "symbol", "bigint",
    "undefined", "null", "void", "never", "any", "unknown", "object",
]);

/**
 * Cache for builtin type checks to avoid repeated resolution.
 */
const builtinTypeCache = new Map<string, boolean>();

/**
 * Project instance for type resolution (set during extraction).
 */
let typeResolutionProject: Project | null = null;

/**
 * Sets the project to use for type resolution.
 */
function setTypeResolutionProject(project: Project): void {
    typeResolutionProject = project;
    builtinTypeCache.clear();
}

/**
 * Checks if a type name is a builtin using TypeScript's type resolution.
 * Builtins are types defined in TypeScript's lib files or @types/node.
 */
function isBuiltinType(typeName: string): boolean {
    // Strip generic parameters
    const baseName = typeName.split("<")[0].trim();

    // Check primitives first (not resolvable)
    if (PRIMITIVE_TYPES.has(baseName)) {
        return true;
    }

    // Check well-known builtins (fast path)
    if (WELL_KNOWN_BUILTINS.has(baseName)) {
        return true;
    }

    // Check cache
    if (builtinTypeCache.has(baseName)) {
        return builtinTypeCache.get(baseName)!;
    }

    // If no project available, assume not builtin (let it be resolved)
    if (!typeResolutionProject) {
        builtinTypeCache.set(baseName, false);
        return false;
    }

    // Try to resolve the type using the project's source files
    // This catches types defined in node_modules/@types/* or typescript/lib/*
    try {
        for (const sourceFile of typeResolutionProject.getSourceFiles()) {
            const filePath = sourceFile.getFilePath();

            // Check interfaces, classes, type aliases, and enums
            const iface = sourceFile.getInterface(baseName);
            if (iface) {
                const isBuiltin = BUILTIN_PATH_PATTERNS.some(pattern => filePath.includes(pattern));
                builtinTypeCache.set(baseName, isBuiltin);
                return isBuiltin;
            }

            const cls = sourceFile.getClass(baseName);
            if (cls) {
                const isBuiltin = BUILTIN_PATH_PATTERNS.some(pattern => filePath.includes(pattern));
                builtinTypeCache.set(baseName, isBuiltin);
                return isBuiltin;
            }

            const typeAlias = sourceFile.getTypeAlias(baseName);
            if (typeAlias) {
                const isBuiltin = BUILTIN_PATH_PATTERNS.some(pattern => filePath.includes(pattern));
                builtinTypeCache.set(baseName, isBuiltin);
                return isBuiltin;
            }

            const enumDecl = sourceFile.getEnum(baseName);
            if (enumDecl) {
                const isBuiltin = BUILTIN_PATH_PATTERNS.some(pattern => filePath.includes(pattern));
                builtinTypeCache.set(baseName, isBuiltin);
                return isBuiltin;
            }
        }
    } catch {
        // If resolution fails, assume not builtin
    }

    builtinTypeCache.set(baseName, false);
    return false;
}

// ============================================================================
// AST-Based Type Reference Collection
// ============================================================================

/**
 * Information about an external type reference with full symbol resolution.
 */
interface ResolvedTypeRef {
    /** Simple type name */
    name: string;
    /** Full type name with namespace */
    fullName: string;
    /** Source file path where the type is declared */
    declarationPath?: string;
    /** Package name (from package.json or path inference) */
    packageName?: string;
}

/**
 * Collects external type references from a Type object using proper AST traversal.
 * This recursively resolves generic type arguments, union/intersection members, etc.
 *
 * @param type The Type object to analyze
 * @param project The ts-morph Project for resolution
 * @param refs Set to collect resolved type references
 * @param visited Set of already visited type IDs to prevent infinite recursion
 */
function collectTypeRefsFromType(
    type: Type,
    project: Project,
    refs: Set<ResolvedTypeRef>,
    visited = new Set<string>()
): void {
    if (!type) return;

    // Wrap in try-catch to handle malformed declarations that can throw in ts-morph
    try {
        // Get a unique ID for this type to prevent infinite recursion
        // Note: getText() can throw on error types - handled by outer try-catch
        const typeId = type.getText();
        if (visited.has(typeId)) return;
        visited.add(typeId);

        // Get the underlying TypeScript type
        const tsType = type.compilerType;

        // Skip primitive types
        if (tsType.flags & (
            ts.TypeFlags.String | ts.TypeFlags.Number | ts.TypeFlags.Boolean |
            ts.TypeFlags.Void | ts.TypeFlags.Undefined | ts.TypeFlags.Null |
            ts.TypeFlags.Never | ts.TypeFlags.Any | ts.TypeFlags.Unknown |
            ts.TypeFlags.BigInt | ts.TypeFlags.ESSymbol
        )) {
            return;
        }

        // Handle union types
        if (type.isUnion()) {
            for (const unionType of type.getUnionTypes()) {
                collectTypeRefsFromType(unionType, project, refs, visited);
            }
            return;
        }

        // Handle intersection types
        if (type.isIntersection()) {
            for (const intersectionType of type.getIntersectionTypes()) {
                collectTypeRefsFromType(intersectionType, project, refs, visited);
            }
            return;
        }

        // Handle array types
        if (type.isArray()) {
            const elementType = type.getArrayElementType();
            if (elementType) {
                collectTypeRefsFromType(elementType, project, refs, visited);
            }
            return;
        }

        // Handle tuple types
        if (type.isTuple()) {
            for (const tupleElement of type.getTupleElements()) {
                collectTypeRefsFromType(tupleElement, project, refs, visited);
            }
            return;
        }

        // Get the symbol for the type
        const symbol = type.getSymbol() || type.getAliasSymbol();
        if (!symbol) return;

        // Get the type name
        const typeName = symbol.getName();

        // Skip anonymous types, primitives, and builtins
        if (!typeName || typeName === "__type" || typeName === "__object" ||
            PRIMITIVE_TYPES.has(typeName) || isBuiltinType(typeName)) {
            return;
        }

        // Get the declaration to find source file
        const declarations = symbol.getDeclarations();
        if (!declarations || declarations.length === 0) return;

        const declaration = declarations[0];
        const sourceFile = declaration.getSourceFile();
        const filePath = sourceFile.getFilePath();

        // Check if this is from a builtin location
        const isBuiltinPath = BUILTIN_PATH_PATTERNS.some(pattern => filePath.includes(pattern));
        if (isBuiltinPath) return;

        // Determine the package name from the source file path
        const packageName = resolvePackageNameFromPath(filePath);

        // Add the resolved reference
        refs.add({
            name: typeName,
            fullName: symbol.getFullyQualifiedName?.() ?? typeName,
            declarationPath: filePath,
            packageName,
        });

        // Recursively process generic type arguments
        const typeArgs = type.getTypeArguments();
        for (const typeArg of typeArgs) {
            collectTypeRefsFromType(typeArg, project, refs, visited);
        }

        // Process base types for classes/interfaces
        const baseTypes = type.getBaseTypes();
        for (const baseType of baseTypes) {
            collectTypeRefsFromType(baseType, project, refs, visited);
        }
    } catch {
        // Silently skip types that fail resolution (malformed declarations, circular refs, etc.)
        // This is non-fatal - we just won't track this dependency
    }
}

/**
 * Resolves the package name from a source file path.
 * Handles node_modules paths and local source files.
 */
function resolvePackageNameFromPath(filePath: string): string | undefined {
    // Check if it's in node_modules
    const nodeModulesIndex = filePath.lastIndexOf("node_modules");
    if (nodeModulesIndex === -1) return undefined;

    // Extract the package path after node_modules
    const afterNodeModules = filePath.substring(nodeModulesIndex + "node_modules".length + 1);

    // Handle scoped packages (@org/package)
    if (afterNodeModules.startsWith("@")) {
        const parts = afterNodeModules.split(/[/\\]/);
        if (parts.length >= 2) {
            return `${parts[0]}/${parts[1]}`;
        }
    } else {
        // Regular package
        const parts = afterNodeModules.split(/[/\\]/);
        if (parts.length >= 1) {
            return parts[0];
        }
    }

    return undefined;
}

/**
 * Collector for type references during extraction.
 * Accumulates all external type references found in the API surface.
 */
class TypeReferenceCollector {
    private refs = new Set<ResolvedTypeRef>();
    private project: Project | null = null;
    private definedTypes = new Set<string>();

    setProject(project: Project): void {
        this.project = project;
    }

    addDefinedType(name: string): void {
        this.definedTypes.add(name.split("<")[0]);
    }

    collectFromType(type: Type): void {
        if (!this.project) return;
        // Try-catch wrapper for safety - ts-morph can throw on malformed types
        try {
            collectTypeRefsFromType(type, this.project, this.refs);
        } catch {
            // Non-fatal - skip types that fail resolution
        }
    }

    getExternalRefs(): ResolvedTypeRef[] {
        // Filter out locally defined types and types without package info
        return Array.from(this.refs).filter(ref =>
            !this.definedTypes.has(ref.name) &&
            ref.packageName !== undefined
        );
    }

    clear(): void {
        this.refs.clear();
        this.definedTypes.clear();
    }
}

/**
 * Global type reference collector instance.
 */
const typeCollector = new TypeReferenceCollector();

// ============================================================================
// Extraction Functions
// ============================================================================

function getDocString(node: JSDocableNode): string | undefined {
    const jsDocs = node.getJsDocs();
    if (!jsDocs?.length) return undefined;

    const comment = jsDocs[0].getComment();
    if (!comment) return undefined;

    const text =
        typeof comment === "string"
            ? comment
            : comment.filter((c): c is NonNullable<typeof c> => c != null)
                .map((c) => (typeof c === "string" ? c : c.getText()))
                .join("");

    const firstLine = text.split("\n")[0].trim();
    return firstLine.length > 120 ? firstLine.substring(0, 117) + "..." : firstLine;
}

function simplifyType(type: string): string {
    if (!type) return type;
    // Remove import() statements
    type = type.replace(/import\([^)]+\)\./g, "");
    // Simplify long types
    if (type.length > 80) {
        type = type.substring(0, 77) + "...";
    }
    return type;
}

function formatParameter(p: ParameterDeclaration): string {
    let sig = p.getName();
    if (p.isOptional()) sig += "?";
    const type = p.getType();
    const typeText = type.getText();
    if (typeText && typeText !== "any") {
        sig += `: ${simplifyType(typeText)}`;
        // Collect type reference for dependency tracking
        typeCollector.collectFromType(type);
    }
    return sig;
}

function extractMethod(method: MethodDeclaration): MethodInfo {
    const params = method.getParameters().map(formatParameter).join(", ");
    const returnType = method.getReturnType();
    const ret = returnType?.getText();

    // Collect return type reference
    if (returnType) {
        typeCollector.collectFromType(returnType);
    }

    const result: MethodInfo = {
        name: method.getName(),
        sig: params,
    };

    if (ret && ret !== "void") result.ret = simplifyType(ret);

    const doc = getDocString(method);
    if (doc) result.doc = doc;

    if (method.isAsync()) result.async = true;
    if (method.isStatic()) result.static = true;

    return result;
}

function extractProperty(prop: PropertyDeclaration): PropertyInfo {
    const type = prop.getType();

    // Collect type reference for dependency tracking
    typeCollector.collectFromType(type);

    const result: PropertyInfo = {
        name: prop.getName(),
        type: simplifyType(type.getText()),
    };

    if (prop.isReadonly()) result.readonly = true;
    if (prop.hasQuestionToken()) result.optional = true;

    const doc = getDocString(prop);
    if (doc) result.doc = doc;

    return result;
}

function extractConstructor(ctor: ConstructorDeclaration): ConstructorInfo {
    return {
        sig: ctor.getParameters().map(formatParameter).join(", "),
    };
}

function extractClass(cls: ClassDeclaration): ClassInfo {
    const name = cls.getName();
    if (!name) throw new Error("Class must have a name");

    const result: ClassInfo = { name };

    // Base class - collect type reference
    const ext = cls.getExtends();
    if (ext) {
        result.extends = ext.getText();
        // Collect base class type
        const baseType = ext.getType();
        typeCollector.collectFromType(baseType);
    }

    // Interfaces - collect type references
    const implExprs = cls.getImplements();
    const impl = implExprs.map((i) => i.getText());
    if (impl.length) {
        result.implements = impl;
        // Collect interface types
        for (const implExpr of implExprs) {
            const implType = implExpr.getType();
            typeCollector.collectFromType(implType);
        }
    }

    // Type parameters
    const typeParams = cls.getTypeParameters().map((t) => t.getText());
    if (typeParams.length) result.typeParams = typeParams.join(", ");

    const doc = getDocString(cls);
    if (doc) result.doc = doc;

    // Constructors
    const ctors = cls
        .getConstructors()
        .filter((c) => c.getScope() !== "private")
        .map(extractConstructor);
    if (ctors.length) result.constructors = ctors;

    // Methods
    const methods = cls
        .getMethods()
        .filter((m) => m.getScope() !== "private" && !m.getName().startsWith("_"))
        .map(extractMethod);
    if (methods.length) result.methods = methods;

    // Properties
    const props = cls
        .getProperties()
        .filter((p) => p.getScope() !== "private" && !p.getName().startsWith("_"))
        .map(extractProperty);
    if (props.length) result.properties = props;

    return result;
}

function extractInterfaceMethod(method: Node): MethodInfo | undefined {
    if (!Node.isMethodSignature(method)) return undefined;

    const params = method.getParameters().map(formatParameter).join(", ");
    const returnType = method.getReturnType();
    const ret = returnType?.getText();

    // Collect return type reference
    if (returnType) {
        typeCollector.collectFromType(returnType);
    }

    const result: MethodInfo = {
        name: method.getName(),
        sig: params,
    };

    if (ret && ret !== "void") result.ret = simplifyType(ret);

    const doc = getDocString(method);
    if (doc) result.doc = doc;

    return result;
}

function extractInterfaceCallableProperty(prop: Node): MethodInfo | undefined {
    if (!Node.isPropertySignature(prop)) return undefined;

    const typeNode = prop.getTypeNode();
    if (!typeNode || !Node.isFunctionTypeNode(typeNode)) return undefined;

    const params = typeNode.getParameters().map(formatParameter).join(", ");
    const returnType = typeNode.getReturnType();
    const ret = returnType?.getText();

    // Collect return type reference
    if (returnType) {
        typeCollector.collectFromType(returnType);
    }

    const result: MethodInfo = {
        name: prop.getName(),
        sig: params,
    };

    if (ret && ret !== "void") result.ret = simplifyType(ret);

    const doc = getDocString(prop);
    if (doc) result.doc = doc;

    return result;
}

function extractInterfaceProperty(prop: Node): PropertyInfo | undefined {
    if (!Node.isPropertySignature(prop)) return undefined;

    const result: PropertyInfo = {
        name: prop.getName(),
        type: simplifyType(prop.getType().getText()),
    };

    if (prop.isReadonly()) result.readonly = true;
    if (prop.hasQuestionToken()) result.optional = true;

    const doc = getDocString(prop);
    if (doc) result.doc = doc;

    return result;
}

function extractInterface(iface: InterfaceDeclaration): InterfaceInfo {
    const result: InterfaceInfo = {
        name: iface.getName(),
    };

    // Extends
    const ext = iface.getExtends().map((e) => e.getText());
    if (ext.length) result.extends = ext.join(", ");

    // Type parameters
    const typeParams = iface.getTypeParameters().map((t) => t.getText());
    if (typeParams.length) result.typeParams = typeParams.join(", ");

    const doc = getDocString(iface);
    if (doc) result.doc = doc;

    // Methods
    const methods: MethodInfo[] = [];
    methods.push(
        ...iface
            .getMethods()
            .filter((m) => !m.getName().startsWith("_"))
            .map((m) => extractInterfaceMethod(m))
            .filter((m): m is MethodInfo => m !== undefined),
    );

    // Properties
    const props: PropertyInfo[] = [];
    for (const prop of iface.getProperties().filter((p) => !p.getName().startsWith("_"))) {
        const callable = extractInterfaceCallableProperty(prop);
        if (callable) {
            methods.push(callable);
            continue;
        }
        const extracted = extractInterfaceProperty(prop);
        if (extracted) props.push(extracted);
    }

    if (methods.length) result.methods = methods;
    if (props.length) result.properties = props;

    return result;
}

function extractEnum(en: EnumDeclaration): EnumInfo {
    return {
        name: en.getName(),
        doc: getDocString(en),
        values: en.getMembers().map((m) => m.getName()),
    };
}

function extractTypeAlias(alias: TypeAliasDeclaration): TypeAliasInfo {
    const type = alias.getType();

    // Collect type reference for dependency tracking
    typeCollector.collectFromType(type);

    return {
        name: alias.getName(),
        type: simplifyType(type.getText()),
        doc: getDocString(alias),
    };
}

function extractFunction(fn: FunctionDeclaration): FunctionInfo | undefined {
    const name = fn.getName();
    if (!name) return undefined;

    const params = fn.getParameters().map(formatParameter).join(", ");
    const returnType = fn.getReturnType();
    const ret = returnType?.getText();

    // Collect return type reference
    if (returnType) {
        typeCollector.collectFromType(returnType);
    }

    const result: FunctionInfo = {
        name,
        sig: params,
    };

    if (ret && ret !== "void") result.ret = simplifyType(ret);

    const doc = getDocString(fn);
    if (doc) result.doc = doc;

    if (fn.isAsync()) result.async = true;

    return result;
}

function extractModule(sourceFile: SourceFile, moduleName: string): ModuleInfo | null {
    const result: ModuleInfo = { name: moduleName };

    // Get exported declarations
    const classes = sourceFile
        .getClasses()
        .filter((c) => c.isExported() && c.getName())
        .map(extractClass);
    if (classes.length) result.classes = classes;

    const interfaces = sourceFile
        .getInterfaces()
        .filter((i) => i.isExported())
        .map(extractInterface);
    if (interfaces.length) result.interfaces = interfaces;

    const enums = sourceFile
        .getEnums()
        .filter((e) => e.isExported())
        .map(extractEnum);
    if (enums.length) result.enums = enums;

    const typeAliases = sourceFile
        .getTypeAliases()
        .filter((t) => t.isExported())
        .map(extractTypeAlias);
    if (typeAliases.length) result.types = typeAliases;

    const functions = sourceFile
        .getFunctions()
        .filter((f) => f.isExported())
        .map(extractFunction)
        .filter((f): f is FunctionInfo => f !== undefined);
    if (functions.length) result.functions = functions;

    // Check if anything was extracted
    if (
        !result.classes &&
        !result.interfaces &&
        !result.enums &&
        !result.types &&
        !result.functions
    ) {
        return null;
    }

    return result;
}

// ============================================================================
// Entry Point Detection
// ============================================================================

interface PackageJson {
    name?: string;
    main?: string;
    module?: string;
    types?: string;
    typings?: string;
    browser?: string | Record<string, string | false>;
    exports?: string | Record<string, unknown>;
}

/**
 * Resolves entry point files from package.json configuration.
 * Prioritizes the "." (root) export - this is what users get with `import { X } from "pkg"`.
 * Only falls back to subpath exports if root export is not found.
 * Supports: exports, types, typings, module, main, browser.
 */
function resolveEntryPointFiles(rootPath: string): ExportEntry[] {
    const pkgPath = path.join(rootPath, "package.json");
    if (!fs.existsSync(pkgPath)) {
        return [];
    }

    const pkg: PackageJson = JSON.parse(fs.readFileSync(pkgPath, "utf8"));
    const entryEntries: ExportEntry[] = [];

    // 1. Modern exports map (highest priority) - prefer "." export
    if (pkg.exports) {
        // First try root export only ("." entry)
        const rootExportPaths = extractExportPaths(pkg.exports, true);
        for (const entry of rootExportPaths) {
            const resolved = resolveToSourceFile(rootPath, entry.filePath);
            if (resolved) entryEntries.push({ exportPath: entry.exportPath, filePath: resolved });
        }

        // Also include all other exports (for subpath tracking)
        const allExportPaths = extractExportPaths(pkg.exports, false);
        for (const entry of allExportPaths) {
            // Skip if already added from root
            if (entryEntries.some(e => e.exportPath === entry.exportPath)) continue;
            const resolved = resolveToSourceFile(rootPath, entry.filePath);
            if (resolved) entryEntries.push({ exportPath: entry.exportPath, filePath: resolved });
        }
    }

    // 2. TypeScript types/typings (these are root exports)
    if (entryEntries.length === 0 && pkg.types) {
        const resolved = resolveToSourceFile(rootPath, pkg.types);
        if (resolved) entryEntries.push({ exportPath: ".", filePath: resolved });
    }
    if (entryEntries.length === 0 && pkg.typings && pkg.typings !== pkg.types) {
        const resolved = resolveToSourceFile(rootPath, pkg.typings);
        if (resolved) entryEntries.push({ exportPath: ".", filePath: resolved });
    }

    // 3. ES module entry (root export)
    if (entryEntries.length === 0 && pkg.module) {
        const resolved = resolveToSourceFile(rootPath, pkg.module);
        if (resolved) entryEntries.push({ exportPath: ".", filePath: resolved });
    }

    // 4. CommonJS entry (root export)
    if (entryEntries.length === 0 && pkg.main) {
        const resolved = resolveToSourceFile(rootPath, pkg.main);
        if (resolved) entryEntries.push({ exportPath: ".", filePath: resolved });
    }

    // 5. Fallback to common entry points
    if (entryEntries.length === 0) {
        const fallbackPaths = [
            "src/index.ts",
            "src/index.tsx",
            "src/index.mts",
            "index.ts",
            "index.tsx",
            "lib/index.ts",
        ];
        for (const fallback of fallbackPaths) {
            const fullPath = path.join(rootPath, fallback);
            if (fs.existsSync(fullPath)) {
                entryEntries.push({ exportPath: ".", filePath: fullPath });
                break;
            }
        }
    }

    return entryEntries;
}

/**
 * Entry for an export path mapping.
 */
interface ExportEntry {
    /** The export subpath (e.g., "." or "./client") */
    exportPath: string;
    /** The resolved source file path */
    filePath: string;
}

/**
 * Extracts path values from package.json exports field.
 * Prioritizes the "." (root) export over subpath exports.
 * @param exports - The exports field from package.json
 * @param rootOnly - If true, only extract from the "." export
 * @returns Array of {exportPath, filePath} pairs
 */
function extractExportPaths(exports: string | Record<string, unknown>, rootOnly: boolean = false): Array<{ exportPath: string; filePath: string }> {
    const results: Array<{ exportPath: string; filePath: string }> = [];

    if (typeof exports === "string") {
        // Simple string export is the root export
        results.push({ exportPath: ".", filePath: exports });
    } else if (typeof exports === "object" && exports !== null) {
        // Object exports map - prioritize "." entry
        const entries = Object.entries(exports);

        // Sort to put "." first, then process
        entries.sort(([keyA], [keyB]) => {
            if (keyA === ".") return -1;
            if (keyB === ".") return 1;
            return keyA.localeCompare(keyB);
        });

        for (const [key, value] of entries) {
            // If rootOnly, skip non-root exports
            if (rootOnly && key !== ".") continue;

            if (typeof value === "string") {
                results.push({ exportPath: key, filePath: value });
            } else if (typeof value === "object" && value !== null) {
                // Nested conditions (types, import, require, default)
                const nested = value as Record<string, unknown>;
                // Prefer types > import > require > default
                const priority = ["types", "import", "require", "default"];
                for (const condKey of priority) {
                    if (typeof nested[condKey] === "string") {
                        results.push({ exportPath: key, filePath: nested[condKey] as string });
                        break; // Only take one per export condition block
                    }
                }
            }
        }
    }

    return results.filter((r) =>
        r.filePath.endsWith(".ts") || r.filePath.endsWith(".d.ts") ||
        r.filePath.endsWith(".js") || r.filePath.endsWith(".mjs")
    );
}

/**
 * Resolves a dist/output path to its corresponding source file.
 * Handles common patterns like dist/ -> src/, types/ -> src/.
 */
function resolveToSourceFile(rootPath: string, outputPath: string): string | null {
    // Normalize path
    let filePath = outputPath.replace(/^\.\//, "");

    // Common output-to-source mappings
    const mappings: Array<{ from: RegExp; to: string }> = [
        { from: /^dist-esm\//, to: "src/" },
        { from: /^dist\//, to: "src/" },
        { from: /^lib\//, to: "src/" },
        { from: /^build\//, to: "src/" },
        { from: /^out\//, to: "src/" },
        { from: /^types\//, to: "src/" },
    ];

    for (const { from, to } of mappings) {
        if (from.test(filePath)) {
            filePath = filePath.replace(from, to);
            break;
        }
    }

    // Convert .d.ts or .js to .ts
    filePath = filePath
        .replace(/\.d\.ts$/, ".ts")
        .replace(/\.js$/, ".ts")
        .replace(/\.mjs$/, ".mts")
        .replace(/\.cjs$/, ".cts");

    const fullPath = path.join(rootPath, filePath);
    if (fs.existsSync(fullPath)) {
        return fullPath;
    }

    // Try without the source mapping (file might be at root)
    const directPath = path.join(rootPath, outputPath.replace(/^\.\//, ""))
        .replace(/\.d\.ts$/, ".ts")
        .replace(/\.js$/, ".ts");
    if (fs.existsSync(directPath)) {
        return directPath;
    }

    return null;
}

/**
 * Information about an exported symbol.
 */
interface ExportedSymbolInfo {
    /** The export subpath (e.g., "." or "./client") */
    exportPath: string;
    /** If re-exported from an external package, the package name */
    reExportedFrom?: string;
}

/**
 * Extracts exported symbol names from entry point files.
 * Returns a map from symbol name to its export info (path and optional re-export source).
 * If a symbol is exported from multiple subpaths, the root export "." takes priority.
 */
function extractExportedSymbols(project: Project, entryEntries: ExportEntry[]): Map<string, ExportedSymbolInfo> {
    // Map from symbol name to export info (priority: "." > other subpaths)
    const exportedSymbols = new Map<string, ExportedSymbolInfo>();

    // Sort entries so "." comes first (gets priority when same symbol exported multiple times)
    const sortedEntries = [...entryEntries].sort((a, b) => {
        if (a.exportPath === ".") return -1;
        if (b.exportPath === ".") return 1;
        return a.exportPath.localeCompare(b.exportPath);
    });

    for (const entry of sortedEntries) {
        const sourceFile = project.getSourceFile(entry.filePath);
        if (!sourceFile) continue;

        // Get directly exported declarations (these are local, not re-exports)
        for (const decl of sourceFile.getExportedDeclarations().keys()) {
            // Only set if not already set (preserves "." priority)
            if (!exportedSymbols.has(decl)) {
                exportedSymbols.set(decl, { exportPath: entry.exportPath });
            }
        }

        // Check export statements for re-exports from external packages
        for (const exportDecl of sourceFile.getExportDeclarations()) {
            const moduleSpecifier = exportDecl.getModuleSpecifierValue();
            const isExternalPackage = moduleSpecifier && !moduleSpecifier.startsWith(".") && !moduleSpecifier.startsWith("/");

            const namedExports = exportDecl.getNamedExports();
            for (const namedExport of namedExports) {
                const name = namedExport.getAliasNode()?.getText() ?? namedExport.getName();
                if (!exportedSymbols.has(name)) {
                    exportedSymbols.set(name, {
                        exportPath: entry.exportPath,
                        reExportedFrom: isExternalPackage ? moduleSpecifier : undefined
                    });
                }
            }

            // Handle `export * from "external-package"` - we can't enumerate these easily
            // without resolving the external package, so we'll mark them when we see them used
            if (exportDecl.isNamespaceExport() && isExternalPackage) {
                // Store a marker for namespace re-exports that we'll use during type marking
                exportedSymbols.set(`__namespace_reexport__${moduleSpecifier}`, {
                    exportPath: entry.exportPath,
                    reExportedFrom: moduleSpecifier
                });
            }
        }
    }

    return exportedSymbols;
}

// ============================================================================
// Package Extraction
// ============================================================================

function detectPackageName(rootPath: string): string {
    const pkgPath = path.join(rootPath, "package.json");
    if (fs.existsSync(pkgPath)) {
        const pkg = JSON.parse(fs.readFileSync(pkgPath, "utf8"));
        return pkg.name || path.basename(rootPath);
    }
    return path.basename(rootPath);
}

export function extractPackage(rootPath: string): ApiIndex {
    const packageName = detectPackageName(rootPath);

    // Find tsconfig or create minimal config
    const tsConfigPath = path.join(rootPath, "tsconfig.json");

    const project = new Project({
        tsConfigFilePath: fs.existsSync(tsConfigPath) ? tsConfigPath : undefined,
        skipAddingFilesFromTsConfig: true,
        compilerOptions: {
            allowJs: true,
            declaration: true,
            emitDeclarationOnly: true,
            skipLibCheck: true,
        },
    });

    // Set project for type resolution (used by isBuiltinType and type collector)
    setTypeResolutionProject(project);
    typeCollector.setProject(project);
    typeCollector.clear();

    // Find source files
    const srcDir = path.join(rootPath, "src");
    const sourceDir = fs.existsSync(srcDir) ? srcDir : rootPath;

    // Add source files
    const patterns = [
        path.join(sourceDir, "**/*.ts"),
        path.join(sourceDir, "**/*.tsx"),
        path.join(sourceDir, "**/*.mts"),
    ];

    for (const pattern of patterns) {
        project.addSourceFilesAtPaths(pattern);
    }

    // Detect entry point files and extract exported symbols
    const entryEntries = resolveEntryPointFiles(rootPath);
    const entryPointSymbols = extractExportedSymbols(project, entryEntries);

    const modules: ModuleInfo[] = [];

    for (const sourceFile of project.getSourceFiles()) {
        const filePath = sourceFile.getFilePath();

        // Skip tests, node_modules, etc (but allow TestFixtures for extractor tests)
        const isTestFixture = filePath.includes("TestFixtures");
        if (
            !isTestFixture &&
            (filePath.includes("node_modules") ||
            filePath.includes(".test.") ||
            filePath.includes(".spec.") ||
            filePath.includes("/test/") ||
            filePath.includes("/tests/"))
        ) {
            continue;
        }

        // Calculate module name
        let moduleName = path.relative(sourceDir, filePath);
        moduleName = moduleName.replace(/\.(ts|tsx|mts)$/, "");
        moduleName = moduleName.replace(/\\/g, "/");
        if (moduleName.endsWith("/index")) {
            moduleName = moduleName.slice(0, -6) || "index";
        }

        const module = extractModule(sourceFile, moduleName);
        if (module) {
            // Mark entry points based on package.json exports
            if (module.classes) {
                for (const cls of module.classes) {
                    const exportInfo = entryPointSymbols.get(cls.name);
                    if (exportInfo !== undefined) {
                        (cls as { entryPoint?: boolean; exportPath?: string; reExportedFrom?: string }).entryPoint = true;
                        (cls as { entryPoint?: boolean; exportPath?: string; reExportedFrom?: string }).exportPath = exportInfo.exportPath;
                        if (exportInfo.reExportedFrom) {
                            (cls as { entryPoint?: boolean; exportPath?: string; reExportedFrom?: string }).reExportedFrom = exportInfo.reExportedFrom;
                        }
                    }
                }
            }
            if (module.interfaces) {
                for (const iface of module.interfaces) {
                    const exportInfo = entryPointSymbols.get(iface.name);
                    if (exportInfo !== undefined) {
                        (iface as { entryPoint?: boolean; exportPath?: string; reExportedFrom?: string }).entryPoint = true;
                        (iface as { entryPoint?: boolean; exportPath?: string; reExportedFrom?: string }).exportPath = exportInfo.exportPath;
                        if (exportInfo.reExportedFrom) {
                            (iface as { entryPoint?: boolean; exportPath?: string; reExportedFrom?: string }).reExportedFrom = exportInfo.reExportedFrom;
                        }
                    }
                }
            }
            if (module.functions) {
                for (const func of module.functions) {
                    const exportInfo = entryPointSymbols.get(func.name);
                    if (exportInfo !== undefined) {
                        (func as { entryPoint?: boolean; exportPath?: string; reExportedFrom?: string }).entryPoint = true;
                        (func as { entryPoint?: boolean; exportPath?: string; reExportedFrom?: string }).exportPath = exportInfo.exportPath;
                        if (exportInfo.reExportedFrom) {
                            (func as { entryPoint?: boolean; exportPath?: string; reExportedFrom?: string }).reExportedFrom = exportInfo.reExportedFrom;
                        }
                    }
                }
            }
            // Mark enums and type aliases too
            if (module.enums) {
                for (const enumInfo of module.enums) {
                    const exportInfo = entryPointSymbols.get(enumInfo.name);
                    if (exportInfo?.reExportedFrom) {
                        (enumInfo as { reExportedFrom?: string }).reExportedFrom = exportInfo.reExportedFrom;
                    }
                }
            }
            if (module.types) {
                for (const typeInfo of module.types) {
                    const exportInfo = entryPointSymbols.get(typeInfo.name);
                    if (exportInfo?.reExportedFrom) {
                        (typeInfo as { reExportedFrom?: string }).reExportedFrom = exportInfo.reExportedFrom;
                    }
                }
            }
            modules.push(module);
        }
    }

    const baseResult: ApiIndex = {
        package: packageName,
        modules: modules.sort((a, b) => a.name.localeCompare(b.name)),
    };

    // Resolve transitive dependencies
    const dependencies = resolveTransitiveDependencies(baseResult, project, rootPath);
    if (dependencies.length > 0) {
        baseResult.dependencies = dependencies;
    }

    return baseResult;
}

// ============================================================================
// Transitive Dependency Resolution
// ============================================================================

/**
 * Gets all type names defined in the API.
 */
function getDefinedTypes(api: ApiIndex): Set<string> {
    const defined = new Set<string>();
    for (const mod of api.modules) {
        for (const cls of mod.classes || []) defined.add(cls.name);
        for (const iface of mod.interfaces || []) defined.add(iface.name);
        for (const en of mod.enums || []) defined.add(en.name);
        for (const t of mod.types || []) defined.add(t.name);
    }
    return defined;
}

/**
 * Finds the package that exports a given type name.
 */
function findTypeInNodeModules(typeName: string, rootPath: string, project: Project): { packageName: string; sourceFile: any } | null {
    const nodeModulesPath = path.join(rootPath, "node_modules");
    if (!fs.existsSync(nodeModulesPath)) return null;

    // Search through project source files for import statements that reference this type
    for (const sourceFile of project.getSourceFiles()) {
        const filePath = sourceFile.getFilePath();
        if (filePath.includes("node_modules")) continue;

        for (const importDecl of sourceFile.getImportDeclarations()) {
            const moduleSpecifier = importDecl.getModuleSpecifierValue();
            if (!moduleSpecifier || moduleSpecifier.startsWith(".")) continue;

            // Check named imports
            const namedImports = importDecl.getNamedImports();
            for (const namedImport of namedImports) {
                const importedName = namedImport.getName();
                const aliasName = namedImport.getAliasNode()?.getText();
                if (importedName === typeName || aliasName === typeName) {
                    // Found! Now resolve the type from the package
                    const resolvedSourceFile = resolveTypeFromPackage(moduleSpecifier, typeName, nodeModulesPath);
                    if (resolvedSourceFile) {
                        return { packageName: moduleSpecifier, sourceFile: resolvedSourceFile };
                    }
                }
            }
        }

        // Also check re-exports in export declarations
        for (const exportDecl of sourceFile.getExportDeclarations()) {
            const moduleSpecifier = exportDecl.getModuleSpecifierValue();
            if (!moduleSpecifier || moduleSpecifier.startsWith(".")) continue;

            const namedExports = exportDecl.getNamedExports();
            for (const namedExport of namedExports) {
                const name = namedExport.getName();
                if (name === typeName) {
                    const resolvedSourceFile = resolveTypeFromPackage(moduleSpecifier, typeName, nodeModulesPath);
                    if (resolvedSourceFile) {
                        return { packageName: moduleSpecifier, sourceFile: resolvedSourceFile };
                    }
                }
            }
        }
    }

    return null;
}

/**
 * Resolves the source file for a type from a package.
 */
function resolveTypeFromPackage(packageName: string, typeName: string, nodeModulesPath: string): any | null {
    // Handle scoped packages
    const packagePath = path.join(nodeModulesPath, packageName);
    if (!fs.existsSync(packagePath)) return null;

    const pkgJsonPath = path.join(packagePath, "package.json");
    if (!fs.existsSync(pkgJsonPath)) return null;

    try {
        const pkg = JSON.parse(fs.readFileSync(pkgJsonPath, "utf8"));

        // Find types entry point
        let typesPath: string | undefined;
        if (pkg.types) typesPath = pkg.types;
        else if (pkg.typings) typesPath = pkg.typings;
        else if (pkg.exports?.["."]?.types) typesPath = pkg.exports["."].types;

        if (!typesPath) return null;

        const fullTypesPath = path.join(packagePath, typesPath);
        if (!fs.existsSync(fullTypesPath)) return null;

        return { path: fullTypesPath, packagePath };
    } catch {
        return null;
    }
}

/**
 * Extracts a type from a dependency package's type definitions.
 */
function extractTypeFromDependency(
    typeName: string,
    typesPath: string,
    packagePath: string
): ClassInfo | InterfaceInfo | EnumInfo | TypeAliasInfo | null {
    try {
        // Create a minimal project for the dependency
        const depProject = new Project({
            skipAddingFilesFromTsConfig: true,
            compilerOptions: {
                allowJs: true,
                declaration: true,
                skipLibCheck: true,
            },
        });

        depProject.addSourceFilesAtPaths(path.join(packagePath, "**/*.d.ts"));

        // Search for the type in all source files
        for (const sourceFile of depProject.getSourceFiles()) {
            // Check classes
            const cls = sourceFile.getClass(typeName);
            if (cls && cls.isExported()) {
                return extractClass(cls);
            }

            // Check interfaces
            const iface = sourceFile.getInterface(typeName);
            if (iface && iface.isExported()) {
                return extractInterface(iface);
            }

            // Check enums
            const en = sourceFile.getEnum(typeName);
            if (en && en.isExported()) {
                return extractEnum(en);
            }

            // Check type aliases
            const typeAlias = sourceFile.getTypeAlias(typeName);
            if (typeAlias && typeAlias.isExported()) {
                return extractTypeAlias(typeAlias);
            }
        }
    } catch {
        // Ignore errors from invalid packages
    }

    return null;
}

/**
 * Resolves transitive dependencies from the API surface.
 * Uses AST-based type collection for accurate dependency tracking.
 */
function resolveTransitiveDependencies(api: ApiIndex, project: Project, rootPath: string): DependencyInfo[] {
    // Register all defined types with the collector (to exclude from dependencies)
    const definedTypes = getDefinedTypes(api);
    for (const typeName of definedTypes) {
        typeCollector.addDefinedType(typeName);
    }

    // Get the AST-collected external type references
    const externalRefs = typeCollector.getExternalRefs();

    if (externalRefs.length === 0) {
        return [];
    }

    // Group by package name
    const typesByPackage = new Map<string, ResolvedTypeRef[]>();
    for (const ref of externalRefs) {
        if (!ref.packageName) continue;

        if (!typesByPackage.has(ref.packageName)) {
            typesByPackage.set(ref.packageName, []);
        }
        typesByPackage.get(ref.packageName)!.push(ref);
    }

    // Build dependency info array
    const dependencies: DependencyInfo[] = [];
    const resolvedTypes = new Map<string, { packageName: string; type: ClassInfo | InterfaceInfo | EnumInfo | TypeAliasInfo }>();

    for (const [packageName, refs] of typesByPackage) {
        // Deduplicate type names
        const uniqueTypeNames = [...new Set(refs.map(r => r.name))];

        // Try to extract full type definitions from the package
        const nodeModulesPath = path.join(rootPath, "node_modules");
        for (const typeName of uniqueTypeNames) {
            const found = findTypeInNodeModules(typeName, rootPath, project);
            if (found?.sourceFile?.path && found?.sourceFile?.packagePath) {
                const extracted = extractTypeFromDependency(typeName, found.sourceFile.path, found.sourceFile.packagePath);
                if (extracted) {
                    resolvedTypes.set(typeName, { packageName, type: extracted });
                }
            }
        }

        const depInfo: DependencyInfo = { package: packageName };

        const classes: ClassInfo[] = [];
        const interfaces: InterfaceInfo[] = [];
        const enums: EnumInfo[] = [];
        const types: TypeAliasInfo[] = [];

        for (const typeName of uniqueTypeNames) {
            const resolved = resolvedTypes.get(typeName);
            if (!resolved) continue;

            const type = resolved.type;
            if ("constructors" in type || "methods" in type && "properties" in type && !("extends" in type && typeof (type as InterfaceInfo).extends === "string")) {
                // Distinguish class from interface by presence of constructors
                if ("constructors" in type) {
                    classes.push(type as ClassInfo);
                } else if ("methods" in type || "properties" in type) {
                    interfaces.push(type as InterfaceInfo);
                }
            } else if ("values" in type) {
                enums.push(type as EnumInfo);
            } else if ("type" in type && typeof (type as TypeAliasInfo).type === "string") {
                types.push(type as TypeAliasInfo);
            }
        }

        if (classes.length > 0) depInfo.classes = classes;
        if (interfaces.length > 0) depInfo.interfaces = interfaces;
        if (enums.length > 0) depInfo.enums = enums;
        if (types.length > 0) depInfo.types = types;

        // Only add if we have at least one resolved type
        if (depInfo.classes || depInfo.interfaces || depInfo.enums || depInfo.types) {
            dependencies.push(depInfo);
        }
    }

    return dependencies.sort((a, b) => a.package.localeCompare(b.package));
}

// ============================================================================
// Formatters
// ============================================================================

export function formatStubs(api: ApiIndex): string {
    const lines: string[] = [
        `// ${api.package} - Public API Surface`,
        "// Extracted by ApiExtractor.TypeScript",
        "",
    ];

    for (const module of api.modules) {
        lines.push(`// Module: ${module.name}`);
        lines.push("");

        // Functions
        for (const fn of module.functions || []) {
            if (fn.doc) lines.push(`/** ${fn.doc} */`);
            const async = fn.async ? "async " : "";
            const ret = fn.ret ? `: ${fn.ret}` : "";
            lines.push(`export ${async}function ${fn.name}(${fn.sig})${ret};`);
            lines.push("");
        }

        // Type aliases
        for (const t of module.types || []) {
            if (t.doc) lines.push(`/** ${t.doc} */`);
            lines.push(`export type ${t.name} = ${t.type};`);
            lines.push("");
        }

        // Enums
        for (const e of module.enums || []) {
            if (e.doc) lines.push(`/** ${e.doc} */`);
            lines.push(`export enum ${e.name} {`);
            lines.push(`    ${e.values.join(", ")}`);
            lines.push("}");
            lines.push("");
        }

        // Interfaces
        for (const iface of module.interfaces || []) {
            if (iface.doc) lines.push(`/** ${iface.doc} */`);
            const ext = iface.extends ? ` extends ${iface.extends}` : "";
            const typeParams = iface.typeParams ? `<${iface.typeParams}>` : "";
            lines.push(`export interface ${iface.name}${typeParams}${ext} {`);

            for (const prop of iface.properties || []) {
                const opt = prop.optional ? "?" : "";
                const ro = prop.readonly ? "readonly " : "";
                lines.push(`    ${ro}${prop.name}${opt}: ${prop.type};`);
            }

            for (const m of iface.methods || []) {
                const async = m.async ? "async " : "";
                const ret = m.ret ? `: ${m.ret}` : "";
                lines.push(`    ${async}${m.name}(${m.sig})${ret};`);
            }

            lines.push("}");
            lines.push("");
        }

        // Classes
        for (const cls of module.classes || []) {
            if (cls.doc) lines.push(`/** ${cls.doc} */`);
            const ext = cls.extends ? ` extends ${cls.extends}` : "";
            const impl = cls.implements?.length ? ` implements ${cls.implements.join(", ")}` : "";
            const typeParams = cls.typeParams ? `<${cls.typeParams}>` : "";
            lines.push(`export class ${cls.name}${typeParams}${ext}${impl} {`);

            for (const prop of cls.properties || []) {
                const opt = prop.optional ? "?" : "";
                const ro = prop.readonly ? "readonly " : "";
                lines.push(`    ${ro}${prop.name}${opt}: ${prop.type};`);
            }

            for (const ctor of cls.constructors || []) {
                lines.push(`    constructor(${ctor.sig});`);
            }

            for (const m of cls.methods || []) {
                const async = m.async ? "async " : "";
                const stat = m.static ? "static " : "";
                const ret = m.ret ? `: ${m.ret}` : "";
                lines.push(`    ${stat}${async}${m.name}(${m.sig})${ret};`);
            }

            if (!cls.properties?.length && !cls.constructors?.length && !cls.methods?.length) {
                lines.push("    // empty");
            }

            lines.push("}");
            lines.push("");
        }
    }

    // Add dependency types if present
    if (api.dependencies && api.dependencies.length > 0) {
        lines.push("");
        lines.push("// ============================================================================");
        lines.push("// Types from Dependencies (referenced in API surface)");
        lines.push("// ============================================================================");
        lines.push("");

        for (const dep of api.dependencies) {
            lines.push(`// From: ${dep.package}`);
            lines.push("");

            // Interfaces
            for (const iface of dep.interfaces || []) {
                if (iface.doc) lines.push(`/** ${iface.doc} */`);
                const ext = iface.extends ? ` extends ${iface.extends}` : "";
                const typeParams = iface.typeParams ? `<${iface.typeParams}>` : "";
                lines.push(`interface ${iface.name}${typeParams}${ext} {`);

                for (const prop of iface.properties || []) {
                    const opt = prop.optional ? "?" : "";
                    const ro = prop.readonly ? "readonly " : "";
                    lines.push(`    ${ro}${prop.name}${opt}: ${prop.type};`);
                }

                for (const m of iface.methods || []) {
                    const ret = m.ret ? `: ${m.ret}` : "";
                    lines.push(`    ${m.name}(${m.sig})${ret};`);
                }

                lines.push("}");
                lines.push("");
            }

            // Classes
            for (const cls of dep.classes || []) {
                if (cls.doc) lines.push(`/** ${cls.doc} */`);
                const ext = cls.extends ? ` extends ${cls.extends}` : "";
                const typeParams = cls.typeParams ? `<${cls.typeParams}>` : "";
                lines.push(`class ${cls.name}${typeParams}${ext} {`);

                for (const prop of cls.properties || []) {
                    const opt = prop.optional ? "?" : "";
                    const ro = prop.readonly ? "readonly " : "";
                    lines.push(`    ${ro}${prop.name}${opt}: ${prop.type};`);
                }

                for (const m of cls.methods || []) {
                    const ret = m.ret ? `: ${m.ret}` : "";
                    lines.push(`    ${m.name}(${m.sig})${ret};`);
                }

                if (!cls.properties?.length && !cls.methods?.length) {
                    lines.push("    // empty");
                }

                lines.push("}");
                lines.push("");
            }

            // Enums
            for (const e of dep.enums || []) {
                if (e.doc) lines.push(`/** ${e.doc} */`);
                lines.push(`enum ${e.name} {`);
                lines.push(`    ${e.values.join(", ")}`);
                lines.push("}");
                lines.push("");
            }

            // Type aliases
            for (const t of dep.types || []) {
                if (t.doc) lines.push(`/** ${t.doc} */`);
                lines.push(`type ${t.name} = ${t.type};`);
                lines.push("");
            }
        }
    }

    return lines.join("\n");
}

export function toJson(api: ApiIndex, pretty: boolean = false): string {
    return pretty ? JSON.stringify(api, null, 2) : JSON.stringify(api);
}

// ============================================================================
// CLI Entry Point
// ============================================================================

function main(): void {
    const args = process.argv.slice(2);

    if (args.length < 1 || args.includes("--help") || args.includes("-h")) {
        console.log(`
TypeScript API Extractor

Usage: extract_api <path> [options]

Options:
    --json        Output JSON format
    --stub        Output TypeScript stub format (default)
    --pretty      Pretty-print JSON output
    --usage <api> <samples>  Analyze usage in samples against API
    --help, -h    Show this help

Examples:
    extract_api ./my-package --json
    extract_api ./my-package --stub
    extract_api --usage api.json ./samples
`);
        process.exit(args.includes("--help") || args.includes("-h") ? 0 : 1);
    }

    // Check for usage analysis mode
    const usageIdx = args.indexOf("--usage");
    if (usageIdx >= 0) {
        if (args.length < usageIdx + 3) {
            console.error("--usage requires <api_json_path> <samples_path>");
            process.exit(1);
        }
        const apiJsonPath = args[usageIdx + 1];
        const samplesPath = path.resolve(args[usageIdx + 2]);

        try {
            const apiJson = fs.readFileSync(apiJsonPath, "utf-8");
            const api = JSON.parse(apiJson) as ApiIndex;
            const usage = analyzeUsage(samplesPath, api);
            console.log(JSON.stringify(usage, null, 2));
        } catch (error) {
            const message = error instanceof Error ? error.message : String(error);
            console.error("Error:", message);
            process.exit(1);
        }
        return;
    }

    const rootPath = path.resolve(args[0]);
    const outputJson = args.includes("--json");
    const pretty = args.includes("--pretty");

    try {
        const api = extractPackage(rootPath);

        if (outputJson) {
            console.log(toJson(api, pretty));
        } else {
            console.log(formatStubs(api));
        }
    } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        console.error("Error:", message);
        process.exit(1);
    }
}

// ============================================================================
// Usage Analysis - Find which API operations are used in samples
// ============================================================================

interface UsageResult {
    file_count: number;
    covered: CoveredOp[];
    uncovered: UncoveredOp[];
    patterns: string[];
}

interface CoveredOp {
    client: string;
    method: string;
    file: string;
    line: number;
}

interface UncoveredOp {
    client: string;
    method: string;
    sig: string;
}

/**
 * Build a variable → client type map for a source file.
 *
 * Tracks patterns:
 *   - const client = new BlobClient(...)           → client maps to BlobClient
 *   - const client: BlobClient = ...               → client maps to BlobClient
 *   - let client: BlobClient                       → client maps to BlobClient
 *   - const client = createBlobClient(...)          → client maps to BlobClient (via function return type map)
 *   - const client = service.getBlobClient(...)     → client maps to BlobClient (via method return type map)
 *   - const blob = storage.blobs                   → client maps to BlobClient (via property type map)
 *
 * All type resolution is driven by API index data — no name-based heuristics.
 */
function buildVarTypeMap(
    sourceFile: SourceFile,
    clientNames: Set<string>,
    propertyTypeMap: Map<string, string>,
    methodReturnTypeMap: Map<string, string>,
    functionReturnTypeMap: Map<string, string>
): Map<string, string> {
    const varTypes = new Map<string, string>();

    sourceFile.forEachDescendant((node) => {
        // Variable declarations: const client = new BlobClient() / const client: BlobClient = ...
        if (Node.isVariableDeclaration(node)) {
            const nameNode = node.getNameNode();
            if (!Node.isIdentifier(nameNode)) return;
            const varName = nameNode.getText();

            // Check type annotation first: const client: BlobClient
            const typeNode = node.getTypeNode();
            if (typeNode) {
                const typeName = typeNode.getText().split("<")[0].trim();
                if (clientNames.has(typeName)) {
                    varTypes.set(varName, typeName);
                    return;
                }
            }

            // Check initializer
            const initializer = node.getInitializer();
            if (initializer) {
                // new BlobClient(...)
                if (Node.isNewExpression(initializer)) {
                    const exprNode = initializer.getExpression();
                    if (Node.isIdentifier(exprNode)) {
                        const name = exprNode.getText();
                        if (clientNames.has(name)) {
                            varTypes.set(varName, name);
                            return;
                        }
                    }
                    if (Node.isPropertyAccessExpression(exprNode)) {
                        const name = exprNode.getName();
                        if (clientNames.has(name)) {
                            varTypes.set(varName, name);
                            return;
                        }
                    }
                }

                // Call expression: createBlobClient() or service.getBlobClient()
                if (Node.isCallExpression(initializer)) {
                    const callExpr = initializer.getExpression();

                    // Standalone function: createBlobClient(...)
                    if (Node.isIdentifier(callExpr)) {
                        const retType = functionReturnTypeMap.get(callExpr.getText());
                        if (retType) {
                            varTypes.set(varName, retType);
                            return;
                        }
                    }

                    // Method call: service.getBlobClient(...) or BlobClient.create(...)
                    if (Node.isPropertyAccessExpression(callExpr)) {
                        const objExpr = callExpr.getExpression();
                        const calledMethodName = callExpr.getName();

                        // Static factory: BlobClient.create(...)
                        if (Node.isIdentifier(objExpr) && clientNames.has(objExpr.getText())) {
                            const staticKey = `${objExpr.getText()}.${calledMethodName}`;
                            const staticRet = methodReturnTypeMap.get(staticKey);
                            varTypes.set(varName, staticRet ?? objExpr.getText());
                            return;
                        }

                        // Instance method: service.getBlobClient(...)
                        if (Node.isIdentifier(objExpr)) {
                            const receiverType = varTypes.get(objExpr.getText());
                            if (receiverType) {
                                const methodKey = `${receiverType.split("<")[0]}.${calledMethodName}`;
                                const retType = methodReturnTypeMap.get(methodKey);
                                if (retType) {
                                    varTypes.set(varName, retType);
                                    return;
                                }
                            }
                        }
                    }
                }

                // Type assertion: expr as BlobClient
                if (Node.isAsExpression(initializer)) {
                    const asTypeNode = initializer.getTypeNode();
                    if (asTypeNode) {
                        const typeName = asTypeNode.getText().split("<")[0].trim();
                        if (clientNames.has(typeName)) {
                            varTypes.set(varName, typeName);
                            return;
                        }
                    }
                }

                // Property access: const blob = storage.blobs → infer from property type map
                if (Node.isPropertyAccessExpression(initializer)) {
                    const objExpr = initializer.getExpression();
                    if (Node.isIdentifier(objExpr)) {
                        const sourceVar = objExpr.getText();
                        const sourceType = varTypes.get(sourceVar);
                        if (sourceType) {
                            const propName = initializer.getName();
                            const propKey = `${sourceType.split("<")[0]}.${propName}`;
                            const propType = propertyTypeMap.get(propKey);
                            if (propType) {
                                varTypes.set(varName, propType);
                                return;
                            }
                        }
                    }
                }
            }
        }

        // Property/field assignments: this.client = new BlobClient(...)
        if (Node.isPropertyDeclaration(node)) {
            const nameNode = node.getNameNode();
            if (Node.isIdentifier(nameNode)) {
                const propName = nameNode.getText();
                const typeNode = node.getTypeNode();
                if (typeNode) {
                    const typeName = typeNode.getText().split("<")[0].trim();
                    if (clientNames.has(typeName)) {
                        varTypes.set(propName, typeName);
                        return;
                    }
                }
                const declInit = node.getInitializer();
                if (declInit && Node.isNewExpression(declInit)) {
                    const exprNode = declInit.getExpression();
                    if (Node.isIdentifier(exprNode)) {
                        const name = exprNode.getText();
                        if (clientNames.has(name)) {
                            varTypes.set(propName, name);
                        }
                    }
                }
            }
        }
    });

    return varTypes;
}

/**
 * Unwrap async wrapper types from a TypeScript return type string.
 * E.g., "Promise<BlobClient>" → "BlobClient".
 */
function unwrapAsyncReturnType(returnType: string): string {
    const wrappers = ["Promise", "PromiseLike", "AsyncIterable", "AsyncIterableIterator"];
    for (const wrapper of wrappers) {
        if (returnType.startsWith(wrapper + "<") && returnType.endsWith(">")) {
            return returnType.slice(wrapper.length + 1, -1);
        }
    }
    return returnType;
}

/**
 * Build a map of (OwnerType.methodName) → return type from API method data.
 * Uses actual method return types from the API index for precise resolution.
 */
function buildMethodReturnTypeMap(
    usageClasses: ClassInfo[],
    usageInterfaces: InterfaceInfo[],
    clientMethods: Map<string, Set<string>>
): Map<string, string> {
    const map = new Map<string, string>();
    for (const cls of usageClasses) {
        for (const method of cls.methods || []) {
            if (method.ret) {
                const returnType = unwrapAsyncReturnType(method.ret).split("<")[0].trim();
                if (clientMethods.has(returnType)) {
                    map.set(`${cls.name.split("<")[0]}.${method.name}`, returnType);
                }
            }
        }
    }
    for (const iface of usageInterfaces) {
        for (const method of iface.methods || []) {
            if (method.ret) {
                const returnType = unwrapAsyncReturnType(method.ret).split("<")[0].trim();
                if (clientMethods.has(returnType)) {
                    map.set(`${iface.name.split("<")[0]}.${method.name}`, returnType);
                }
            }
        }
    }
    return map;
}

/**
 * Build a map of functionName → return type from API function data.
 * For module-level functions that return client types.
 */
function buildFunctionReturnTypeMap(
    api: ApiIndex,
    clientMethods: Map<string, Set<string>>
): Map<string, string> {
    const map = new Map<string, string>();
    for (const mod of api.modules) {
        for (const func of mod.functions || []) {
            if (func.ret) {
                const returnType = unwrapAsyncReturnType(func.ret).split("<")[0].trim();
                if (clientMethods.has(returnType)) {
                    map.set(func.name, returnType);
                }
            }
        }
    }
    return map;
}

/**
 * Build a map of (OwnerType.propertyName) → client type name from API property data.
 * Uses actual property return types from the API index for precise resolution.
 */
function buildPropertyTypeMap(
    usageClasses: ClassInfo[],
    usageInterfaces: InterfaceInfo[],
    clientMethods: Map<string, Set<string>>
): Map<string, string> {
    const map = new Map<string, string>();
    for (const cls of usageClasses) {
        for (const prop of cls.properties || []) {
            const returnType = prop.type.split("<")[0].trim();
            if (clientMethods.has(returnType)) {
                map.set(`${cls.name.split("<")[0]}.${prop.name}`, returnType);
            }
        }
    }
    for (const iface of usageInterfaces) {
        for (const prop of iface.properties || []) {
            const returnType = prop.type.split("<")[0].trim();
            if (clientMethods.has(returnType)) {
                map.set(`${iface.name.split("<")[0]}.${prop.name}`, returnType);
            }
        }
    }
    return map;
}

function analyzeUsage(samplesPath: string, api: ApiIndex): UsageResult {
    const allClasses: ClassInfo[] = [];
    const allInterfaces: InterfaceInfo[] = [];
    const allTypeNames = new Set<string>();
    const interfaceNames = new Set<string>();

    for (const mod of api.modules) {
        for (const cls of mod.classes || []) {
            allClasses.push(cls);
            allTypeNames.add(cls.name.split("<")[0]);
        }
        for (const iface of mod.interfaces || []) {
            allInterfaces.push(iface);
            interfaceNames.add(iface.name.split("<")[0]);
            allTypeNames.add(iface.name.split("<")[0]);
        }
    }

    const interfaceImplementers = new Map<string, ClassInfo[]>();
    for (const cls of allClasses) {
        for (const iface of cls.implements || []) {
            const ifaceName = iface.split("<")[0];
            const list = interfaceImplementers.get(ifaceName) ?? [];
            list.push(cls);
            interfaceImplementers.set(ifaceName, list);
        }
    }

    const interfacesByName = new Map<string, InterfaceInfo>();
    for (const iface of allInterfaces) {
        const name = iface.name.split("<")[0];
        if (!interfacesByName.has(name)) {
            interfacesByName.set(name, iface);
        }
    }

    const references = new Map<string, Set<string>>();
    for (const cls of allClasses) {
        references.set(cls.name.split("<")[0], getReferencedTypes(cls, allTypeNames));
    }
    for (const iface of allInterfaces) {
        references.set(iface.name.split("<")[0], getReferencedTypesForInterface(iface, allTypeNames));
    }

    const referencedBy = new Map<string, number>();
    for (const refs of references.values()) {
        for (const target of refs) {
            referencedBy.set(target, (referencedBy.get(target) ?? 0) + 1);
        }
    }

    const operationTypes = new Set<string>();
    for (const cls of allClasses) {
        if ((cls.methods?.length ?? 0) > 0) {
            operationTypes.add(cls.name.split("<")[0]);
        }
    }
    for (const iface of allInterfaces) {
        if ((iface.methods?.length ?? 0) > 0) {
            operationTypes.add(iface.name.split("<")[0]);
        }
    }

    // Root classes: entry points (from package exports) with methods, or unreferenced types with operations
    let rootClasses = allClasses.filter((cls) => {
        const name = cls.name.split("<")[0];
        const hasOperations = (cls.methods?.length ?? 0) > 0;
        const refs = references.get(name);
        const referencesOperations = refs ? Array.from(refs).some((r) => operationTypes.has(r)) : false;
        return (cls.entryPoint && hasOperations) || (!referencedBy.has(name) && (hasOperations || referencesOperations));
    });

    if (rootClasses.length === 0) {
        rootClasses = allClasses.filter((cls) => {
            const name = cls.name.split("<")[0];
            const hasOperations = (cls.methods?.length ?? 0) > 0;
            const refs = references.get(name);
            const referencesOperations = refs ? Array.from(refs).some((r) => operationTypes.has(r)) : false;
            return hasOperations || referencesOperations;
        });
    }

    const reachable = new Set<string>();
    const queue: string[] = [];

    for (const cls of rootClasses) {
        const name = cls.name.split("<")[0];
        if (!reachable.has(name)) {
            reachable.add(name);
            queue.push(name);
        }
    }

    while (queue.length > 0) {
        const current = queue.shift()!;
        const refs = references.get(current);
        if (refs) {
            for (const ref of refs) {
                if (!reachable.has(ref)) {
                    reachable.add(ref);
                    queue.push(ref);
                }
            }
        }

        if (interfaceNames.has(current)) {
            for (const impl of interfaceImplementers.get(current) ?? []) {
                const implName = impl.name.split("<")[0];
                if (!reachable.has(implName)) {
                    reachable.add(implName);
                    queue.push(implName);
                }
            }
        }
    }

    const usageClasses = allClasses.filter(
        (cls) => reachable.has(cls.name.split("<")[0]) && (cls.methods?.length ?? 0) > 0
    );

    const usageInterfaces = allInterfaces.filter(
        (iface) => reachable.has(iface.name.split("<")[0]) && (iface.methods?.length ?? 0) > 0
    );

    // Build map of client methods from API
    const clientMethods: Map<string, Set<string>> = new Map();

    for (const cls of usageClasses) {
        const methods = new Set<string>();
        for (const method of cls.methods || []) {
            methods.add(method.name);
        }
        if (methods.size > 0) {
            if (!clientMethods.has(cls.name)) {
                clientMethods.set(cls.name, methods);
            }
        }
    }

    for (const iface of usageInterfaces) {
        const methods = new Set<string>();
        for (const method of iface.methods || []) {
            methods.add(method.name);
        }
        if (methods.size > 0) {
            if (!clientMethods.has(iface.name)) {
                clientMethods.set(iface.name, methods);
            }
        }
    }

    if (clientMethods.size === 0) {
        return { file_count: 0, covered: [], uncovered: [], patterns: [] };
    }

    const covered: CoveredOp[] = [];
    const seenOps: Set<string> = new Set();
    const patterns: Set<string> = new Set();
    let fileCount = 0;

    // Build set of known client type names for local type inference
    const clientNames = new Set(clientMethods.keys());

    // Build property type map from API data for precise subclient resolution
    const propertyTypeMap = buildPropertyTypeMap(usageClasses, usageInterfaces, clientMethods);

    // Build method and function return type maps from API data for precise factory/getter resolution
    const methodReturnTypeMap = buildMethodReturnTypeMap(usageClasses, usageInterfaces, clientMethods);
    const functionReturnTypeMap = buildFunctionReturnTypeMap(api, clientMethods);

    // Create a project for parsing samples
    const project = new Project({
        compilerOptions: { allowJs: true, noEmit: true },
        skipFileDependencyResolution: true,
    });

    // Find all TS/JS files in samples
    const files = findFiles(samplesPath, [".ts", ".js", ".mjs", ".tsx", ".jsx"])
        .filter(f => !f.includes("node_modules") && !f.includes("/dist/") && !f.endsWith(".d.ts"));

    for (const filePath of files) {
        fileCount++;

        try {
            const sourceFile = project.addSourceFileAtPath(filePath);
            const relPath = path.relative(samplesPath, filePath);

            // Build variable → client type map for this file
            const varTypes = buildVarTypeMap(sourceFile, clientNames, propertyTypeMap, methodReturnTypeMap, functionReturnTypeMap);

            // Use ts-morph to find all call expressions
            sourceFile.forEachDescendant((node) => {
                if (Node.isCallExpression(node)) {
                    const expr = node.getExpression();
                    if (Node.isPropertyAccessExpression(expr)) {
                        const methodName = expr.getName();
                        const line = node.getStartLineNumber();

                        // Strategy 1: Resolve receiver type from local variable tracking
                        let resolvedClient: string | undefined;
                        const receiver = expr.getExpression();
                        if (Node.isIdentifier(receiver)) {
                            const varType = varTypes.get(receiver.getText());
                            if (varType && clientMethods.has(varType)) {
                                const methods = clientMethods.get(varType)!;
                                if (methods.has(methodName)) {
                                    resolvedClient = varType;
                                }
                            }
                        } else if (Node.isPropertyAccessExpression(receiver)) {
                            // this.client.method() or obj.client.method()
                            const propName = receiver.getName();
                            const varType = varTypes.get(propName);
                            if (varType && clientMethods.has(varType)) {
                                const methods = clientMethods.get(varType)!;
                                if (methods.has(methodName)) {
                                    resolvedClient = varType;
                                }
                            }
                        } else if (Node.isCallExpression(receiver)) {
                            // getClient().method() - resolve return type from API data
                            const innerExpr = receiver.getExpression();

                            if (Node.isIdentifier(innerExpr)) {
                                // Standalone function: createClient().method()
                                const retType = functionReturnTypeMap.get(innerExpr.getText());
                                if (retType && clientMethods.has(retType)) {
                                    const methods = clientMethods.get(retType)!;
                                    if (methods.has(methodName)) {
                                        resolvedClient = retType;
                                    }
                                }
                            } else if (Node.isPropertyAccessExpression(innerExpr)) {
                                // Instance method: service.getClient().method()
                                const chainedObj = innerExpr.getExpression();
                                const chainedMethodName = innerExpr.getName();

                                // Static factory: ClientType.create().method()
                                if (Node.isIdentifier(chainedObj) && clientNames.has(chainedObj.getText())) {
                                    const staticKey = `${chainedObj.getText()}.${chainedMethodName}`;
                                    const retType = methodReturnTypeMap.get(staticKey) ?? chainedObj.getText();
                                    if (clientMethods.has(retType)) {
                                        const methods = clientMethods.get(retType)!;
                                        if (methods.has(methodName)) {
                                            resolvedClient = retType;
                                        }
                                    }
                                } else if (Node.isIdentifier(chainedObj)) {
                                    // service.getClient().method()
                                    const receiverType = varTypes.get(chainedObj.getText());
                                    if (receiverType) {
                                        const methodKey = `${receiverType.split("<")[0]}.${chainedMethodName}`;
                                        const retType = methodReturnTypeMap.get(methodKey);
                                        if (retType && clientMethods.has(retType)) {
                                            const methods = clientMethods.get(retType)!;
                                            if (methods.has(methodName)) {
                                                resolvedClient = retType;
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        if (resolvedClient) {
                            const key = `${resolvedClient}.${methodName}`;
                            if (!seenOps.has(key)) {
                                seenOps.add(key);
                                covered.push({ client: resolvedClient, method: methodName, file: relPath, line });
                            }
                        }
                    }
                }
            });

            // Detect patterns
            detectPatterns(sourceFile, patterns);

            project.removeSourceFile(sourceFile);
        } catch {
            // Skip files that can't be parsed
        }
    }

    // Build bidirectional interface ↔ implementation mapping for coverage cross-referencing
    const ifaceToImplNames = new Map<string, string[]>();
    const implToIfaceNames = new Map<string, string[]>();
    for (const cls of allClasses) {
        for (const iface of cls.implements || []) {
            const ifaceName = iface.split("<")[0];
            const impls = ifaceToImplNames.get(ifaceName) ?? [];
            impls.push(cls.name);
            ifaceToImplNames.set(ifaceName, impls);

            const ifaces = implToIfaceNames.get(cls.name) ?? [];
            ifaces.push(ifaceName);
            implToIfaceNames.set(cls.name, ifaces);
        }
    }

    // Build uncovered list with interface/implementation cross-referencing
    const uncovered: UncoveredOp[] = [];
    for (const [clientName, methods] of clientMethods) {
        for (const method of methods) {
            const key = `${clientName}.${method}`;
            if (seenOps.has(key)) {
                continue;
            }

            // Check if covered through an interface/implementation relationship
            let coveredViaRelated = false;

            // If this is an implementation, check if any of its interfaces has the method covered
            const implementedIfaces = implToIfaceNames.get(clientName);
            if (implementedIfaces) {
                coveredViaRelated = implementedIfaces.some(
                    (iface) => seenOps.has(`${iface}.${method}`),
                );
            }

            // If this is an interface, check if any implementation has the method covered
            if (!coveredViaRelated) {
                const implementations = ifaceToImplNames.get(clientName);
                if (implementations) {
                    coveredViaRelated = implementations.some(
                        (impl) => seenOps.has(`${impl}.${method}`),
                    );
                }
            }

            if (!coveredViaRelated) {
                uncovered.push({
                    client: clientName,
                    method,
                    sig: `${method}(...)`,
                });
            }
        }
    }

    return {
        file_count: fileCount,
        covered,
        uncovered,
        patterns: Array.from(patterns).sort(),
    };
}

function getReferencedTypes(cls: ClassInfo, allTypeNames: Set<string>): Set<string> {
    const refs = new Set<string>();

    if (cls.extends) {
        const baseName = cls.extends.split("<")[0];
        if (allTypeNames.has(baseName)) {
            refs.add(baseName);
        }
    }

    for (const iface of cls.implements || []) {
        const ifaceName = iface.split("<")[0];
        if (allTypeNames.has(ifaceName)) {
            refs.add(ifaceName);
        }
    }

    for (const method of cls.methods || []) {
        for (const typeName of allTypeNames) {
            if (method.sig.includes(typeName) || (method.ret?.includes(typeName) ?? false)) {
                refs.add(typeName);
            }
        }
    }

    for (const prop of cls.properties || []) {
        for (const typeName of allTypeNames) {
            if (prop.type.includes(typeName)) {
                refs.add(typeName);
            }
        }
    }

    return refs;
}

function getReferencedTypesForInterface(iface: InterfaceInfo, allTypeNames: Set<string>): Set<string> {
    const refs = new Set<string>();

    if (iface.extends) {
        const bases = iface.extends.split(",").map((entry) => entry.trim()).filter((entry) => entry.length > 0);
        for (const entry of bases) {
            const baseName = entry.split("<")[0];
            if (allTypeNames.has(baseName)) {
                refs.add(baseName);
            }
        }
    }

    for (const method of iface.methods || []) {
        for (const typeName of allTypeNames) {
            if (method.sig.includes(typeName) || (method.ret?.includes(typeName) ?? false)) {
                refs.add(typeName);
            }
        }
    }

    for (const prop of iface.properties || []) {
        for (const typeName of allTypeNames) {
            if (prop.type.includes(typeName)) {
                refs.add(typeName);
            }
        }
    }

    return refs;
}

function detectPatterns(sourceFile: SourceFile, patterns: Set<string>): void {
    // Detect patterns using purely structural AST analysis — no keyword matching
    sourceFile.forEachDescendant((node) => {
        if (Node.isAwaitExpression(node)) {
            patterns.add("async");
        }
        if (Node.isTryStatement(node)) {
            patterns.add("error-handling");
        }
        if (Node.isForOfStatement(node) && node.isAwaited()) {
            patterns.add("streaming");
        }
    });
}

function findFiles(dir: string, extensions: string[]): string[] {
    const results: string[] = [];

    if (!fs.existsSync(dir)) return results;

    const entries = fs.readdirSync(dir, { withFileTypes: true });
    for (const entry of entries) {
        const fullPath = path.join(dir, entry.name);
        if (entry.isDirectory()) {
            results.push(...findFiles(fullPath, extensions));
        } else if (extensions.some(ext => entry.name.endsWith(ext))) {
            results.push(fullPath);
        }
    }
    return results;
}

// Run CLI - ES module entry point
main();
