// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using PublicApiGraphEngine.Contracts;

namespace PublicApiGraphEngine.TypeScript;

/// <summary>
/// Formats an ApiIndex as human-readable TypeScript stub syntax.
/// Supports smart truncation that prioritizes client classes and their dependencies.
/// </summary>
public static class TypeScriptFormatter
{
    /// <summary>
    /// Builds a dictionary from items by key, keeping only the first item for each key
    /// to safely handle duplicate names across modules.
    /// </summary>
    private static Dictionary<string, T> SafeToDictionary<T>(IEnumerable<T> items, Func<T, string> keySelector)
    {
        var dict = new Dictionary<string, T>();
        foreach (var item in items)
            dict.TryAdd(keySelector(item), item);
        return dict;
    }

    public static string Format(ApiIndex index) => Format(index, int.MaxValue);

    /// <summary>
    /// Formats with coverage awareness: compact summary of covered ops, full signatures for uncovered.
    /// This provides ~70% token savings while maintaining complete context for generation.
    /// </summary>
    public static string FormatWithCoverage(ApiIndex index, UsageIndex coverage, int maxLength)
    {
        var sb = new StringBuilder();

        var deprecatedOperations = index.Modules
            .SelectMany(m => (m.Classes ?? [])
                .SelectMany(c => (c.Methods ?? [])
                    .Where(method => method.IsDeprecated == true)
                    .Select(method => (Client: c.Name, Method: method.Name))))
            .ToHashSet();

        List<UncoveredOperation> deprecatedUncovered = [];
        List<UncoveredOperation> filteredUncovered = [];
        foreach (var op in coverage.UncoveredOperations)
        {
            if (deprecatedOperations.Contains((op.ClientType, op.Operation)))
                deprecatedUncovered.Add(op);
            else
                filteredUncovered.Add(op);
        }

        var filteredCoverage = coverage with { UncoveredOperations = filteredUncovered };

        var uncoveredByClient = CoverageFormatter.AppendCoverageSummary(sb, filteredCoverage);
        if (uncoveredByClient is null)
            return sb.ToString();

        if (deprecatedUncovered.Count > 0)
        {
            sb.AppendLine("// Deprecated API (intentionally excluded from uncovered generation targets):");
            foreach (var op in deprecatedUncovered.OrderBy(o => o.ClientType).ThenBy(o => o.Operation))
            {
                sb.AppendLine($"//   {op.ClientType}.{op.Operation}");
            }
            sb.AppendLine();
        }

        var allClasses = index.Modules.SelectMany(m => m.Classes ?? []).ToList();
        var classesWithUncovered = allClasses.Where(c => uncoveredByClient.ContainsKey(c.Name)).ToList();

        // Build lookups for dependency tracking
        var allInterfaces = index.Modules.SelectMany(m => m.Interfaces ?? []).ToList();
        HashSet<string> allTypeNames = [];
        foreach (var c in allClasses) allTypeNames.Add(c.Name);
        foreach (var i in allInterfaces) allTypeNames.Add(i.Name);
        var allClassesByName = SafeToDictionary(allClasses, c => c.Name);
        var allIfacesByName = SafeToDictionary(allInterfaces, i => i.Name);

        HashSet<string> includedClasses = [];
        HashSet<string> reusableDeps = [];

        foreach (var cls in classesWithUncovered)
        {
            if (includedClasses.Contains(cls.Name))
                continue;

            // Filter to show only uncovered methods for client classes
            var filteredClass = cls;
            if (uncoveredByClient.TryGetValue(cls.Name, out var uncoveredOps))
            {
                filteredClass = cls with
                {
                    Methods = cls.Methods?
                        .Where(m => uncoveredOps.Contains(m.Name))
                        .ToList() ?? []
                };
            }

            var classContent = FormatClassToString(filteredClass);

            if (sb.Length + classContent.Length > maxLength - 100 && includedClasses.Count > 0)
            {
                sb.AppendLine($"// ... truncated ({classesWithUncovered.Count - includedClasses.Count} classes omitted)");
                break;
            }

            sb.Append(classContent);
            includedClasses.Add(cls.Name);

            // Include supporting model/option types referenced by uncovered operations
            filteredClass.CollectReferencedTypes(allTypeNames, reusableDeps);
            foreach (var depName in reusableDeps)
            {
                if (includedClasses.Contains(depName))
                    continue;

                if (allClassesByName.TryGetValue(depName, out var depClass))
                {
                    var depContent = FormatClassToString(depClass);
                    if (sb.Length + depContent.Length > maxLength - 100)
                        break;
                    sb.Append(depContent);
                    includedClasses.Add(depName);
                }
                else if (allIfacesByName.TryGetValue(depName, out var depIface))
                {
                    var depContent = FormatInterface(depIface);
                    if (sb.Length + depContent.Length > maxLength - 100)
                        break;
                    sb.Append(depContent);
                    includedClasses.Add(depName);
                }
            }
        }

        // Include dependency types from external packages if space permits
        if (index.Dependencies?.Count > 0)
        {
            foreach (var dep in index.Dependencies)
            {
                foreach (var cls in dep.Classes ?? [])
                {
                    if (includedClasses.Contains(cls.Name))
                        continue;

                    var depContent = FormatClassToString(cls);
                    if (sb.Length + depContent.Length > maxLength - 100)
                        break;
                    sb.Append(depContent);
                    includedClasses.Add(cls.Name);
                }

                foreach (var iface in dep.Interfaces ?? [])
                {
                    if (includedClasses.Contains(iface.Name))
                        continue;

                    var depContent = FormatInterface(iface);
                    if (sb.Length + depContent.Length > maxLength - 100)
                        break;
                    sb.Append(depContent);
                    includedClasses.Add(iface.Name);
                }
            }
        }

        return sb.ToString();
    }

    private static string FormatClassToString(ClassInfo cls)
    {
        return FormatClass(cls);
    }

    /// <summary>
    /// Formats the API surface with smart truncation that prioritizes client classes.
    /// Groups exported symbols by their export subpath (e.g., ".", "./client").
    /// </summary>
    public static string Format(ApiIndex index, int maxLength)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"// {index.Package} - Public API Surface");
        sb.AppendLine("// Graphed by PublicApiGraphEngine.TypeScript");
        sb.AppendLine();

        // Get all classes, interfaces, and enums for prioritization
        var allClasses = index.Modules.SelectMany(m => m.Classes ?? []).ToList();
        var allInterfaces = index.Modules.SelectMany(m => m.Interfaces ?? []).ToList();
        var allEnums = index.Modules.SelectMany(m => m.Enums ?? []).ToList();
        var allTypes = index.Modules.SelectMany(m => m.Types ?? []).ToList();
        var allFunctions = index.Modules.SelectMany(m => m.Functions ?? []).ToList();

        // Build set of all type names for dependency tracking
        HashSet<string> allTypeNames = [];
        foreach (var c in allClasses) allTypeNames.Add(c.Name);
        foreach (var i in allInterfaces) allTypeNames.Add(i.Name);
        foreach (var e in allEnums) allTypeNames.Add(e.Name);
        foreach (var t in allTypes) allTypeNames.Add(t.Name);

        // Pre-build dictionaries for O(1) lookups instead of O(n) FirstOrDefault
        var interfacesByName = SafeToDictionary(allInterfaces, i => i.Name);
        var enumsByName = SafeToDictionary(allEnums, e => e.Name);
        var typesByName = SafeToDictionary(allTypes, t => t.Name);

        // Build dependency graph for classes, interfaces, and type aliases
        var typeDeps = new Dictionary<string, HashSet<string>>();
        HashSet<string> reusableDeps2 = [];
        foreach (var cls in allClasses)
        {
            cls.CollectReferencedTypes(allTypeNames, reusableDeps2);
            typeDeps[cls.Name] = new HashSet<string>(reusableDeps2);
        }
        foreach (var iface in allInterfaces)
        {
            iface.CollectReferencedTypes(allTypeNames, reusableDeps2);
            typeDeps[iface.Name] = new HashSet<string>(reusableDeps2);
        }
        foreach (var t in allTypes)
        {
            t.CollectReferencedTypes(allTypeNames, reusableDeps2);
            typeDeps[t.Name] = new HashSet<string>(reusableDeps2);
        }

        // Group entry points by export path
        HashSet<string> exportPaths = [];
        foreach (var cls in allClasses.Where(c => c.ExportPath is not null))
            exportPaths.Add(cls.ExportPath!);
        foreach (var iface in allInterfaces.Where(i => i.ExportPath is not null))
            exportPaths.Add(iface.ExportPath!);
        foreach (var fn in allFunctions.Where(f => f.ExportPath is not null))
            exportPaths.Add(fn.ExportPath!);

        // Sort export paths: "." first, then alphabetically
        var sortedExportPaths = exportPaths
            .OrderBy(p => p == "." ? "" : p)
            .ToList();

        int totalItems = allClasses.Count + allInterfaces.Count + allEnums.Count + allTypes.Count + allFunctions.Count;
        int includedItems = 0;
        HashSet<string> includedTypeNames = [];

        // Format by export path sections if we have multiple paths
        if (sortedExportPaths.Count > 1)
        {
            foreach (var exportPath in sortedExportPaths)
            {
                if (sb.Length >= maxLength) break;

                var importPath = exportPath == "."
                    ? index.Package
                    : $"{index.Package}/{(exportPath.StartsWith("./", StringComparison.Ordinal) ? exportPath[2..] : exportPath)}";

                sb.AppendLine($"// ============================================================================");
                sb.AppendLine($"// import {{ ... }} from \"{importPath}\"");
                sb.AppendLine($"// ============================================================================");
                sb.AppendLine();

                // Get classes for this export path
                var pathClasses = allClasses.Where(c => c.ExportPath == exportPath).ToList();
                var prioritizedClasses = GetPrioritizedClasses(pathClasses, typeDeps);

                foreach (var cls in prioritizedClasses)
                {
                    if (sb.Length >= maxLength) break;
                    if (includedTypeNames.Contains(cls.Name)) continue;

                    var classStr = FormatClass(cls);
                    if (sb.Length + classStr.Length > maxLength - 100 && includedItems > 0)
                        break;

                    sb.Append(classStr);
                    includedTypeNames.Add(cls.Name);
                    includedItems++;
                }

                // Get interfaces for this export path
                foreach (var iface in allInterfaces.Where(i => i.ExportPath == exportPath))
                {
                    if (sb.Length >= maxLength) break;
                    if (includedTypeNames.Contains(iface.Name)) continue;

                    var ifaceStr = FormatInterface(iface);
                    if (sb.Length + ifaceStr.Length > maxLength - 100 && includedItems > 0)
                        break;

                    sb.Append(ifaceStr);
                    includedTypeNames.Add(iface.Name);
                    includedItems++;
                }

                // Get functions for this export path
                foreach (var fn in allFunctions.Where(f => f.ExportPath == exportPath))
                {
                    if (sb.Length >= maxLength) break;

                    var fnStr = FormatFunction(fn);
                    if (sb.Length + fnStr.Length > maxLength - 100 && includedItems > 0)
                        break;

                    sb.Append(fnStr);
                    includedItems++;
                }
            }

            // Include non-exported dependencies if space permits
            sb.AppendLine($"// ============================================================================");
            sb.AppendLine($"// Supporting Types (not directly exported)");
            sb.AppendLine($"// ============================================================================");
            sb.AppendLine();
        }
        else
        {
            // Original behavior: no export path grouping
            var prioritizedClasses = GetPrioritizedClasses(allClasses, typeDeps);

            // First pass: Include client classes and their dependencies
            foreach (var cls in prioritizedClasses)
            {
                if (sb.Length >= maxLength) break;

                // Include this class
                var classStr = FormatClass(cls);
                if (sb.Length + classStr.Length > maxLength - 100 && includedItems > 0)
                    break;

                sb.Append(classStr);
                includedTypeNames.Add(cls.Name);
                includedItems++;

                // Include its dependencies (interfaces, enums, types)
                foreach (var depName in typeDeps.GetValueOrDefault(cls.Name, []))
                {
                    if (includedTypeNames.Contains(depName)) continue;
                    if (sb.Length >= maxLength) break;

                    // Try to find and include the dependency
                    if (interfacesByName.TryGetValue(depName, out var iface))
                    {
                        var ifaceStr = FormatInterface(iface);
                        if (sb.Length + ifaceStr.Length <= maxLength)
                        {
                            sb.Append(ifaceStr);
                            includedTypeNames.Add(depName);
                            includedItems++;
                        }
                        continue;
                    }

                    if (enumsByName.TryGetValue(depName, out var enumDef))
                    {
                        var enumStr = FormatEnum(enumDef);
                        if (sb.Length + enumStr.Length <= maxLength)
                        {
                            sb.Append(enumStr);
                            includedTypeNames.Add(depName);
                            includedItems++;
                        }
                        continue;
                    }

                    if (typesByName.TryGetValue(depName, out var typeDef))
                    {
                        var typeStr = FormatTypeAlias(typeDef);
                        if (sb.Length + typeStr.Length <= maxLength)
                        {
                            sb.Append(typeStr);
                            includedTypeNames.Add(depName);
                            includedItems++;
                        }
                    }
                }
            }
        }

        // Include remaining interfaces if space permits
        foreach (var iface in allInterfaces.Where(i => !includedTypeNames.Contains(i.Name)))
        {
            if (sb.Length >= maxLength) break;
            var ifaceStr = FormatInterface(iface);
            if (sb.Length + ifaceStr.Length <= maxLength)
            {
                sb.Append(ifaceStr);
                includedTypeNames.Add(iface.Name);
                includedItems++;
            }
        }

        // Include remaining enums if space permits
        foreach (var enumDef in allEnums.Where(e => !includedTypeNames.Contains(e.Name)))
        {
            if (sb.Length >= maxLength) break;
            var enumStr = FormatEnum(enumDef);
            if (sb.Length + enumStr.Length <= maxLength)
            {
                sb.Append(enumStr);
                includedTypeNames.Add(enumDef.Name);
                includedItems++;
            }
        }

        // Include remaining functions if space permits (limit to first 20)
        int funcCount = 0;
        foreach (var fn in allFunctions.Where(f => f.ExportPath is null).Take(20))
        {
            if (sb.Length >= maxLength) break;
            var fnStr = FormatFunction(fn);
            if (sb.Length + fnStr.Length <= maxLength)
            {
                sb.Append(fnStr);
                includedItems++;
                funcCount++;
            }
        }

        // Include dependency types if present and space permits
        if (index.Dependencies is not null && index.Dependencies.Count > 0 && sb.Length < maxLength)
        {
            sb.AppendLine();
            sb.AppendLine("// ============================================================================");
            sb.AppendLine("// Types from Dependencies (referenced in API surface)");
            sb.AppendLine("// ============================================================================");
            sb.AppendLine();

            foreach (var dep in index.Dependencies)
            {
                if (sb.Length >= maxLength) break;
                if (dep.IsNode) continue;

                sb.AppendLine($"// From: {dep.Package}");
                sb.AppendLine();

                foreach (var iface in dep.Interfaces ?? [])
                {
                    if (sb.Length >= maxLength) break;
                    var ifaceStr = FormatInterface(iface, exportKeyword: false);
                    if (sb.Length + ifaceStr.Length <= maxLength)
                    {
                        sb.Append(ifaceStr);
                        includedItems++;
                    }
                }

                foreach (var cls in dep.Classes ?? [])
                {
                    if (sb.Length >= maxLength) break;
                    var clsStr = FormatClass(cls, exportKeyword: false);
                    if (sb.Length + clsStr.Length <= maxLength)
                    {
                        sb.Append(clsStr);
                        includedItems++;
                    }
                }

                // Enums
                foreach (var e in dep.Enums ?? [])
                {
                    if (sb.Length >= maxLength) break;
                    var enumStr = FormatEnum(e, exportKeyword: false);
                    if (sb.Length + enumStr.Length <= maxLength)
                    {
                        sb.Append(enumStr);
                        includedItems++;
                    }
                }

                // Type aliases
                foreach (var t in dep.Types ?? [])
                {
                    if (sb.Length >= maxLength) break;
                    var typeStr = FormatTypeAlias(t, exportKeyword: false);
                    if (sb.Length + typeStr.Length <= maxLength)
                    {
                        sb.Append(typeStr);
                        includedItems++;
                    }
                }
            }
        }

        // Add truncation notice if needed
        if (includedItems < totalItems)
        {
            sb.AppendLine($"// ... truncated ({totalItems - includedItems} items omitted)");
        }

        return sb.ToString();
    }

    private static List<ClassInfo> GetPrioritizedClasses(List<ClassInfo> classes, Dictionary<string, HashSet<string>> deps)
    {
        List<ClassInfo> result = [];
        HashSet<string> added = [];

        // Pre-build dictionary for O(1) lookups
        var classesByName = SafeToDictionary(classes, c => c.Name);

        // Add client classes first (priority 0)
        var clientClasses = classes.Where(c => c.IsClientType).OrderBy(c => c.Name).ToList();
        foreach (var client in clientClasses)
        {
            result.Add(client);
            added.Add(client.Name);
        }

        // Add classes that clients depend on
        foreach (var client in clientClasses)
        {
            foreach (var depName in deps.GetValueOrDefault(client.Name, []))
            {
                if (classesByName.TryGetValue(depName, out var depClass) && !added.Contains(depClass.Name))
                {
                    result.Add(depClass);
                    added.Add(depClass.Name);
                }
            }
        }

        // Add remaining classes sorted by priority
        foreach (var cls in classes.Where(c => !added.Contains(c.Name)).OrderBy(c => c.TruncationPriority).ThenBy(c => c.Name))
        {
            result.Add(cls);
        }

        return result;
    }

    private static string FormatClass(ClassInfo cls, bool exportKeyword = true)
    {
        var sb = new StringBuilder();
        if (cls.IsDeprecated == true)
            sb.AppendLine($"/** @deprecated{(string.IsNullOrWhiteSpace(cls.DeprecatedMessage) ? "" : $" {cls.DeprecatedMessage}")} */");
        if (!string.IsNullOrEmpty(cls.Doc))
            sb.AppendLine($"/** {cls.Doc} */");
        var ext = !string.IsNullOrEmpty(cls.Extends) ? $" extends {cls.Extends}" : "";
        var impl = cls.Implements?.Count > 0 ? $" implements {string.Join(", ", cls.Implements)}" : "";
        var typeParams = !string.IsNullOrEmpty(cls.TypeParams) ? $"<{cls.TypeParams}>" : "";
        var export = exportKeyword ? "export " : "";
        sb.AppendLine($"{export}class {cls.Name}{typeParams}{ext}{impl} {{");

        foreach (var prop in cls.Properties ?? [])
        {
            if (prop.IsDeprecated == true)
                sb.AppendLine($"    /** @deprecated{(string.IsNullOrWhiteSpace(prop.DeprecatedMessage) ? "" : $" {prop.DeprecatedMessage}")} */");
            var opt = prop.Optional == true ? "?" : "";
            var ro = prop.Readonly == true ? "readonly " : "";
            sb.AppendLine($"    {ro}{prop.Name}{opt}: {prop.Type};");
        }

        foreach (var ctor in cls.Constructors ?? [])
        {
            if (ctor.IsDeprecated == true)
                sb.AppendLine($"    /** @deprecated{(string.IsNullOrWhiteSpace(ctor.DeprecatedMessage) ? "" : $" {ctor.DeprecatedMessage}")} */");
            sb.AppendLine($"    constructor({ctor.Sig});");
        }

        foreach (var m in cls.Methods ?? [])
        {
            if (m.IsDeprecated == true)
                sb.AppendLine($"    /** @deprecated{(string.IsNullOrWhiteSpace(m.DeprecatedMessage) ? "" : $" {m.DeprecatedMessage}")} */");
            var async = m.Async == true ? "async " : "";
            var stat = m.Static == true ? "static " : "";
            var ret = !string.IsNullOrEmpty(m.Ret) ? $": {m.Ret}" : "";
            sb.AppendLine($"    {stat}{async}{m.Name}({m.Sig}){ret};");
        }

        if ((cls.Properties?.Count ?? 0) == 0 && (cls.Constructors?.Count ?? 0) == 0 && (cls.Methods?.Count ?? 0) == 0)
        {
            sb.AppendLine("    // empty");
        }

        sb.AppendLine("}");
        sb.AppendLine();
        return sb.ToString();
    }

    private static string FormatInterface(InterfaceInfo iface, bool exportKeyword = true)
    {
        var sb = new StringBuilder();
        if (iface.IsDeprecated == true)
            sb.AppendLine($"/** @deprecated{(string.IsNullOrWhiteSpace(iface.DeprecatedMessage) ? "" : $" {iface.DeprecatedMessage}")} */");
        if (!string.IsNullOrEmpty(iface.Doc))
            sb.AppendLine($"/** {iface.Doc} */");
        var ext = iface.Extends?.Count > 0 ? $" extends {string.Join(", ", iface.Extends)}" : "";
        var typeParams = !string.IsNullOrEmpty(iface.TypeParams) ? $"<{iface.TypeParams}>" : "";
        var export = exportKeyword ? "export " : "";
        sb.AppendLine($"{export}interface {iface.Name}{typeParams}{ext} {{");

        foreach (var prop in iface.Properties ?? [])
        {
            if (prop.IsDeprecated == true)
                sb.AppendLine($"    /** @deprecated{(string.IsNullOrWhiteSpace(prop.DeprecatedMessage) ? "" : $" {prop.DeprecatedMessage}")} */");
            var opt = prop.Optional == true ? "?" : "";
            var ro = prop.Readonly == true ? "readonly " : "";
            sb.AppendLine($"    {ro}{prop.Name}{opt}: {prop.Type};");
        }

        foreach (var m in iface.Methods ?? [])
        {
            if (m.IsDeprecated == true)
                sb.AppendLine($"    /** @deprecated{(string.IsNullOrWhiteSpace(m.DeprecatedMessage) ? "" : $" {m.DeprecatedMessage}")} */");
            var async = m.Async == true ? "async " : "";
            var ret = !string.IsNullOrEmpty(m.Ret) ? $": {m.Ret}" : "";
            sb.AppendLine($"    {async}{m.Name}({m.Sig}){ret};");
        }

        sb.AppendLine("}");
        sb.AppendLine();
        return sb.ToString();
    }

    private static string FormatEnum(EnumInfo e, bool exportKeyword = true)
    {
        var sb = new StringBuilder();
        if (e.IsDeprecated == true)
            sb.AppendLine($"/** @deprecated{(string.IsNullOrWhiteSpace(e.DeprecatedMessage) ? "" : $" {e.DeprecatedMessage}")} */");
        if (!string.IsNullOrEmpty(e.Doc))
            sb.AppendLine($"/** {e.Doc} */");
        var export = exportKeyword ? "export " : "";
        sb.AppendLine($"{export}enum {e.Name} {{");
        if (e.Values is not null)
            sb.AppendLine($"    {string.Join(", ", e.Values)}");
        sb.AppendLine("}");
        sb.AppendLine();
        return sb.ToString();
    }

    private static string FormatTypeAlias(TypeAliasInfo t, bool exportKeyword = true)
    {
        var sb = new StringBuilder();
        if (t.IsDeprecated == true)
            sb.AppendLine($"/** @deprecated{(string.IsNullOrWhiteSpace(t.DeprecatedMessage) ? "" : $" {t.DeprecatedMessage}")} */");
        if (!string.IsNullOrEmpty(t.Doc))
            sb.AppendLine($"/** {t.Doc} */");
        var export = exportKeyword ? "export " : "";
        sb.AppendLine($"{export}type {t.Name} = {t.Type};");
        sb.AppendLine();
        return sb.ToString();
    }

    private static string FormatFunction(FunctionInfo fn)
    {
        var sb = new StringBuilder();
        if (fn.IsDeprecated == true)
            sb.AppendLine($"/** @deprecated{(string.IsNullOrWhiteSpace(fn.DeprecatedMessage) ? "" : $" {fn.DeprecatedMessage}")} */");
        if (!string.IsNullOrEmpty(fn.Doc))
            sb.AppendLine($"/** {fn.Doc} */");
        var async = fn.Async == true ? "async " : "";
        var ret = !string.IsNullOrEmpty(fn.Ret) ? $": {fn.Ret}" : "";
        sb.AppendLine($"export {async}function {fn.Name}({fn.Sig}){ret};");
        sb.AppendLine();
        return sb.ToString();
    }
}
