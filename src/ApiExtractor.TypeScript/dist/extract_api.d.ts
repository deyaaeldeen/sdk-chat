#!/usr/bin/env node
/**
 * Extract public API surface from TypeScript/JavaScript packages.
 * Uses ts-morph for proper TypeScript parsing.
 */
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
    exportPath?: string;
    reExportedFrom?: string;
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
    exportPath?: string;
    reExportedFrom?: string;
    extends?: string;
    typeParams?: string;
    doc?: string;
    methods?: MethodInfo[];
    properties?: PropertyInfo[];
}
export interface EnumInfo {
    name: string;
    reExportedFrom?: string;
    doc?: string;
    values: string[];
}
export interface TypeAliasInfo {
    name: string;
    type: string;
    reExportedFrom?: string;
    doc?: string;
}
export interface FunctionInfo {
    name: string;
    entryPoint?: boolean;
    exportPath?: string;
    reExportedFrom?: string;
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
export declare function extractPackage(rootPath: string): ApiIndex;
export declare function formatStubs(api: ApiIndex): string;
export declare function toJson(api: ApiIndex, pretty?: boolean): string;
//# sourceMappingURL=extract_api.d.ts.map