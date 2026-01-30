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
    extends?: string;
    typeParams?: string;
    doc?: string;
    methods?: MethodInfo[];
    properties?: PropertyInfo[];
}
export interface EnumInfo {
    name: string;
    doc?: string;
    values: string[];
}
export interface TypeAliasInfo {
    name: string;
    type: string;
    doc?: string;
}
export interface FunctionInfo {
    name: string;
    sig: string;
    ret?: string;
    doc?: string;
    async?: boolean;
}
export interface ModuleInfo {
    name: string;
    classes?: ClassInfo[];
    interfaces?: InterfaceInfo[];
    enums?: EnumInfo[];
    types?: TypeAliasInfo[];
    functions?: FunctionInfo[];
}
export interface ApiIndex {
    package: string;
    modules: ModuleInfo[];
}
export declare function extractPackage(rootPath: string): ApiIndex;
export declare function formatStubs(api: ApiIndex): string;
export declare function toJson(api: ApiIndex, pretty?: boolean): string;
//# sourceMappingURL=extract_api.d.ts.map