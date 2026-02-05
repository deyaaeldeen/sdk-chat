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
    const type = p.getType().getText();
    if (type && type !== "any") sig += `: ${simplifyType(type)}`;
    return sig;
}

function extractMethod(method: MethodDeclaration): MethodInfo {
    const params = method.getParameters().map(formatParameter).join(", ");
    const ret = method.getReturnType()?.getText();

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

function extractConstructor(ctor: ConstructorDeclaration): ConstructorInfo {
    return {
        sig: ctor.getParameters().map(formatParameter).join(", "),
    };
}

function extractClass(cls: ClassDeclaration): ClassInfo {
    const name = cls.getName();
    if (!name) throw new Error("Class must have a name");

    const result: ClassInfo = { name };

    // Base class
    const ext = cls.getExtends();
    if (ext) result.extends = ext.getText();

    // Interfaces
    const impl = cls.getImplements().map((i) => i.getText());
    if (impl.length) result.implements = impl;

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
    const ret = method.getReturnType()?.getText();

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
    const ret = typeNode.getReturnType()?.getText();

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
    return {
        name: alias.getName(),
        type: simplifyType(alias.getType().getText()),
        doc: getDocString(alias),
    };
}

function extractFunction(fn: FunctionDeclaration): FunctionInfo | undefined {
    const name = fn.getName();
    if (!name) return undefined;

    const params = fn.getParameters().map(formatParameter).join(", ");
    const ret = fn.getReturnType()?.getText();

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
            modules.push(module);
        }
    }

    return {
        package: packageName,
        modules: modules.sort((a, b) => a.name.localeCompare(b.name)),
    };
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

    // Client classes are always roots - they're SDK entry points even if referenced by options/builders
    const isClientClass = (name: string) => name.endsWith("Client") || name.endsWith("AsyncClient");

    let rootClasses = allClasses.filter((cls) => {
        const name = cls.name.split("<")[0];
        const hasOperations = (cls.methods?.length ?? 0) > 0;
        const refs = references.get(name);
        const referencesOperations = refs ? Array.from(refs).some((r) => operationTypes.has(r)) : false;
        return isClientClass(name) || (!referencedBy.has(name) && (hasOperations || referencesOperations));
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

            // Use ts-morph to find all call expressions
            sourceFile.forEachDescendant((node) => {
                if (Node.isCallExpression(node)) {
                    const expr = node.getExpression();
                    if (Node.isPropertyAccessExpression(expr)) {
                        const methodName = expr.getName();
                        const line = node.getStartLineNumber();

                        // Check if this method belongs to a known client
                        for (const [clientName, methods] of clientMethods) {
                            if (methods.has(methodName)) {
                                const key = `${clientName}.${methodName}`;
                                if (!seenOps.has(key)) {
                                    seenOps.add(key);
                                    covered.push({
                                        client: clientName,
                                        method: methodName,
                                        file: relPath,
                                        line,
                                    });
                                }
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

    // Build uncovered list
    const uncovered: UncoveredOp[] = [];
    for (const [clientName, methods] of clientMethods) {
        for (const method of methods) {
            const key = `${clientName}.${method}`;
            if (!seenOps.has(key)) {
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
    const text = sourceFile.getFullText().toLowerCase();

    // Check for async/await using ts-morph
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

    // Keyword-based patterns
    if (text.includes("credential") || text.includes("apikey") || text.includes("bearer")) {
        patterns.add("authentication");
    }
    if (text.includes("page") || text.includes("cursor") || text.includes("nextlink")) {
        patterns.add("pagination");
    }
    if (text.includes("retry") || text.includes("p-retry")) {
        patterns.add("retry");
    }
    if (text.includes("options") || text.includes("config")) {
        patterns.add("configuration");
    }
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
