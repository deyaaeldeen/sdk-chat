// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using ApiExtractor.Contracts;

namespace ApiExtractor.TypeScript;

/// <summary>
/// Formats an ApiIndex as human-readable TypeScript stub syntax.
/// Supports smart truncation that prioritizes client classes and their dependencies.
/// </summary>
public static class TypeScriptFormatter
{
    public static string Format(ApiIndex index) => Format(index, int.MaxValue);
    
    /// <summary>
    /// Formats with coverage awareness: compact summary of covered ops, full signatures for uncovered.
    /// This provides ~70% token savings while maintaining complete context for generation.
    /// </summary>
    public static string FormatWithCoverage(ApiIndex index, UsageIndex coverage, int maxLength)
    {
        var sb = new StringBuilder();
        
        // Section 1: Compact summary of what's already covered
        var coveredByClient = coverage.CoveredOperations
            .GroupBy(op => op.ClientType)
            .ToDictionary(g => g.Key, g => g.Select(op => op.Operation).Distinct().ToList());
        
        if (coveredByClient.Count > 0)
        {
            var totalCovered = coverage.CoveredOperations.Count;
            sb.AppendLine($"// ALREADY COVERED ({totalCovered} calls across {coverage.FileCount} files) - DO NOT DUPLICATE:");
            foreach (var (client, ops) in coveredByClient.OrderBy(kv => kv.Key))
            {
                sb.AppendLine($"//   {client}: {string.Join(", ", ops.Take(10))}{(ops.Count > 10 ? $" (+{ops.Count - 10} more)" : "")}");
            }
            sb.AppendLine();
        }
        
        // Section 2: Full signatures for types containing uncovered operations
        var uncoveredByClient = coverage.UncoveredOperations
            .GroupBy(op => op.ClientType)
            .ToDictionary(g => g.Key, g => g.Select(op => op.Operation).ToHashSet());
        
        if (uncoveredByClient.Count == 0)
        {
            sb.AppendLine("// All operations are covered in existing samples.");
            return sb.ToString();
        }
        
        sb.AppendLine($"// UNCOVERED API ({coverage.UncoveredOperations.Count} operations) - Generate samples for these:");
        sb.AppendLine();
        
        // Format only classes that have uncovered operations
        var allClasses = index.Modules.SelectMany(m => m.Classes ?? []).ToList();
        var classesWithUncovered = allClasses.Where(c => uncoveredByClient.ContainsKey(c.Name)).ToList();
        
        var includedClasses = new HashSet<string>();
        var currentLength = sb.Length;
        
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
            
            if (currentLength + classContent.Length > maxLength - 100 && includedClasses.Count > 0)
            {
                sb.AppendLine($"// ... truncated ({classesWithUncovered.Count - includedClasses.Count} classes omitted)");
                break;
            }
            
            sb.Append(classContent);
            currentLength += classContent.Length;
            includedClasses.Add(cls.Name);
        }
        
        return sb.ToString();
    }
    
    private static string FormatClassToString(ClassInfo cls)
    {
        return FormatClass(cls);
    }
    
    /// <summary>
    /// Formats the API surface with smart truncation that prioritizes client classes.
    /// </summary>
    public static string Format(ApiIndex index, int maxLength)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"// {index.Package} - Public API Surface");
        sb.AppendLine("// Extracted by ApiExtractor.TypeScript");
        sb.AppendLine();

        // Get all classes, interfaces, and enums for prioritization
        var allClasses = index.Modules.SelectMany(m => m.Classes ?? []).ToList();
        var allInterfaces = index.Modules.SelectMany(m => m.Interfaces ?? []).ToList();
        var allEnums = index.Modules.SelectMany(m => m.Enums ?? []).ToList();
        var allTypes = index.Modules.SelectMany(m => m.Types ?? []).ToList();
        var allFunctions = index.Modules.SelectMany(m => m.Functions ?? []).ToList();
        
        // Build set of all type names for dependency tracking
        var allTypeNames = new HashSet<string>();
        foreach (var c in allClasses) allTypeNames.Add(c.Name);
        foreach (var i in allInterfaces) allTypeNames.Add(i.Name);
        foreach (var e in allEnums) allTypeNames.Add(e.Name);
        foreach (var t in allTypes) allTypeNames.Add(t.Name);
        
        // Build dependency graph for classes
        var classDeps = new Dictionary<string, HashSet<string>>();
        foreach (var cls in allClasses)
        {
            classDeps[cls.Name] = cls.GetReferencedTypes(allTypeNames);
        }
        
        // Prioritize classes: clients first, then their dependencies, then others
        var prioritizedClasses = GetPrioritizedClasses(allClasses, classDeps);
        
        // Format with budget awareness
        int totalItems = allClasses.Count + allInterfaces.Count + allEnums.Count + allTypes.Count + allFunctions.Count;
        int includedItems = 0;
        var includedTypeNames = new HashSet<string>();
        
        // First pass: Include client classes and their dependencies
        foreach (var cls in prioritizedClasses)
        {
            if (sb.Length >= maxLength) break;
            
            // Include this class
            var classStr = FormatClass(cls);
            if (sb.Length + classStr.Length > maxLength && includedItems > 0)
                break;
                
            sb.Append(classStr);
            includedTypeNames.Add(cls.Name);
            includedItems++;
            
            // Include its dependencies (interfaces, enums, types)
            foreach (var depName in classDeps.GetValueOrDefault(cls.Name, []))
            {
                if (includedTypeNames.Contains(depName)) continue;
                if (sb.Length >= maxLength) break;
                
                // Try to find and include the dependency
                var iface = allInterfaces.FirstOrDefault(i => i.Name == depName);
                if (iface != null)
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
                
                var enumDef = allEnums.FirstOrDefault(e => e.Name == depName);
                if (enumDef != null)
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
                
                var typeDef = allTypes.FirstOrDefault(t => t.Name == depName);
                if (typeDef != null)
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
        
        // Second pass: Include remaining interfaces if space permits
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
        
        // Third pass: Include remaining enums if space permits
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
        
        // Fourth pass: Include functions if space permits (limit to first 20)
        int funcCount = 0;
        foreach (var fn in allFunctions.Take(20))
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
        
        // Add truncation notice if needed
        if (includedItems < totalItems)
        {
            sb.AppendLine($"// ... truncated ({totalItems - includedItems} items omitted)");
        }

        return sb.ToString();
    }
    
    private static List<ClassInfo> GetPrioritizedClasses(List<ClassInfo> classes, Dictionary<string, HashSet<string>> deps)
    {
        var result = new List<ClassInfo>();
        var added = new HashSet<string>();
        
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
                var depClass = classes.FirstOrDefault(c => c.Name == depName);
                if (depClass != null && !added.Contains(depClass.Name))
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
    
    private static string FormatClass(ClassInfo cls)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(cls.Doc))
            sb.AppendLine($"/** {cls.Doc} */");
        var ext = !string.IsNullOrEmpty(cls.Extends) ? $" extends {cls.Extends}" : "";
        var impl = cls.Implements?.Count > 0 ? $" implements {string.Join(", ", cls.Implements)}" : "";
        var typeParams = !string.IsNullOrEmpty(cls.TypeParams) ? $"<{cls.TypeParams}>" : "";
        sb.AppendLine($"export class {cls.Name}{typeParams}{ext}{impl} {{");

        foreach (var prop in cls.Properties ?? [])
        {
            var opt = prop.Optional == true ? "?" : "";
            var ro = prop.Readonly == true ? "readonly " : "";
            sb.AppendLine($"    {ro}{prop.Name}{opt}: {prop.Type};");
        }

        foreach (var ctor in cls.Constructors ?? [])
        {
            sb.AppendLine($"    constructor({ctor.Sig});");
        }

        foreach (var m in cls.Methods ?? [])
        {
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
    
    private static string FormatInterface(InterfaceInfo iface)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(iface.Doc))
            sb.AppendLine($"/** {iface.Doc} */");
        var ext = !string.IsNullOrEmpty(iface.Extends) ? $" extends {iface.Extends}" : "";
        var typeParams = !string.IsNullOrEmpty(iface.TypeParams) ? $"<{iface.TypeParams}>" : "";
        sb.AppendLine($"export interface {iface.Name}{typeParams}{ext} {{");

        foreach (var prop in iface.Properties ?? [])
        {
            var opt = prop.Optional == true ? "?" : "";
            var ro = prop.Readonly == true ? "readonly " : "";
            sb.AppendLine($"    {ro}{prop.Name}{opt}: {prop.Type};");
        }

        foreach (var m in iface.Methods ?? [])
        {
            var async = m.Async == true ? "async " : "";
            var ret = !string.IsNullOrEmpty(m.Ret) ? $": {m.Ret}" : "";
            sb.AppendLine($"    {async}{m.Name}({m.Sig}){ret};");
        }

        sb.AppendLine("}");
        sb.AppendLine();
        return sb.ToString();
    }
    
    private static string FormatEnum(EnumInfo e)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(e.Doc))
            sb.AppendLine($"/** {e.Doc} */");
        sb.AppendLine($"export enum {e.Name} {{");
        if (e.Values != null)
            sb.AppendLine($"    {string.Join(", ", e.Values)}");
        sb.AppendLine("}");
        sb.AppendLine();
        return sb.ToString();
    }
    
    private static string FormatTypeAlias(TypeAliasInfo t)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(t.Doc))
            sb.AppendLine($"/** {t.Doc} */");
        sb.AppendLine($"export type {t.Name} = {t.Type};");
        sb.AppendLine();
        return sb.ToString();
    }
    
    private static string FormatFunction(FunctionInfo fn)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(fn.Doc))
            sb.AppendLine($"/** {fn.Doc} */");
        var async = fn.Async == true ? "async " : "";
        var ret = !string.IsNullOrEmpty(fn.Ret) ? $": {fn.Ret}" : "";
        sb.AppendLine($"export {async}function {fn.Name}({fn.Sig}){ret};");
        sb.AppendLine();
        return sb.ToString();
    }
}
