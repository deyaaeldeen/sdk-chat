// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using ApiExtractor.Contracts;

namespace ApiExtractor.DotNet;

/// <summary>
/// Formats an ApiIndex as human-readable C# syntax.
/// Supports smart truncation that prioritizes clients and their dependencies.
/// </summary>
public static class CSharpFormatter
{
    /// <summary>
    /// Formats the full API surface.
    /// </summary>
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
        
        // Format only types that have uncovered operations
        var allTypes = index.GetAllTypes().ToList();
        var allTypeNames = allTypes.Select(t => t.Name.Split('<')[0]).ToHashSet();
        
        // Find types with uncovered operations and their dependencies
        var typesWithUncovered = allTypes
            .Where(t => uncoveredByClient.ContainsKey(t.Name))
            .ToList();
        
        var neededTypes = new HashSet<string>();
        foreach (var t in typesWithUncovered)
        {
            neededTypes.Add(t.Name);
            foreach (var dep in t.GetReferencedTypes(allTypeNames))
                neededTypes.Add(dep);
        }
        
        // Handle potential duplicate type names (same name in different namespaces)
        var typesByName = new Dictionary<string, TypeInfo>();
        foreach (var t in allTypes)
        {
            typesByName.TryAdd(t.Name, t);
        }
        var includedTypes = new HashSet<string>();
        var currentLength = sb.Length;
        
        // Include types with uncovered operations first, then their dependencies
        var orderedTypes = allTypes
            .Where(t => neededTypes.Contains(t.Name))
            .OrderBy(t => uncoveredByClient.ContainsKey(t.Name) ? 0 : 1)
            .ThenBy(t => t.TruncationPriority)
            .ToList();
        
        foreach (var type in orderedTypes)
        {
            if (includedTypes.Contains(type.Name))
                continue;
            
            // Filter members to show only uncovered operations for client types
            var filteredType = type;
            if (uncoveredByClient.TryGetValue(type.Name, out var uncoveredOps))
            {
                // Only include uncovered members
                filteredType = type with
                {
                    Members = type.Members?
                        .Where(m => m.Kind != "method" || uncoveredOps.Contains(m.Name))
                        .ToList() ?? []
                };
            }
            
            var typeContent = FormatTypesWithNamespace(new[] { filteredType }, index);
            
            if (currentLength + typeContent.Length > maxLength - 100 && includedTypes.Count > 0)
            {
                sb.AppendLine($"// ... truncated ({orderedTypes.Count - includedTypes.Count} types omitted, budget exceeded)");
                break;
            }
            
            sb.Append(typeContent);
            currentLength += typeContent.Length;
            includedTypes.Add(type.Name);
        }
        
        return sb.ToString();
    }
    
    /// <summary>
    /// Formats with smart truncation to fit within budget.
    /// Prioritizes: Clients → Their dependencies → Options → Enums → Models → Rest
    /// </summary>
    public static string Format(ApiIndex index, int maxLength)
    {
        var sb = new StringBuilder();
        
        sb.AppendLine($"// {index.Package} - Public API Surface");
        sb.AppendLine();
        
        // Build type lookup and dependency graph
        var allTypes = index.GetAllTypes().ToList();
        var allTypeNames = allTypes.Select(t => t.Name.Split('<')[0]).ToHashSet();
        
        // Handle potential duplicate type names (same name in different namespaces)
        var typesByName = new Dictionary<string, TypeInfo>();
        foreach (var t in allTypes)
        {
            // First one wins - typically the more important one is defined first
            typesByName.TryAdd(t.Name, t);
        }
        var typesByNamespace = allTypes.GroupBy(t => GetNamespace(t, index)).ToDictionary(g => g.Key, g => g.ToList());
        
        // Prioritize types for inclusion
        var orderedTypes = GetPrioritizedTypes(allTypes, allTypeNames);
        
        // Track what we've included
        var includedTypes = new HashSet<string>();
        var currentLength = sb.Length;
        
        // Include types by priority, pulling in dependencies
        foreach (var type in orderedTypes)
        {
            if (includedTypes.Contains(type.Name))
                continue;
            
            // Calculate size of this type + its dependencies
            var typesToAdd = new List<TypeInfo> { type };
            var deps = type.GetReferencedTypes(allTypeNames);
            foreach (var depName in deps)
            {
                if (!includedTypes.Contains(depName) && typesByName.TryGetValue(depName, out var depType))
                    typesToAdd.Add(depType);
            }
            
            // Format these types
            var typeContent = FormatTypes(typesToAdd, GetNamespace(type, index));
            
            // Check if we have room
            if (currentLength + typeContent.Length > maxLength - 100 && includedTypes.Count > 0)
            {
                // No room - we're done
                sb.AppendLine($"// ... truncated ({allTypes.Count - includedTypes.Count} types omitted, budget exceeded)");
                break;
            }
            
            sb.Append(typeContent);
            currentLength += typeContent.Length;
            
            foreach (var t in typesToAdd)
                includedTypes.Add(t.Name);
        }
        
        return sb.ToString();
    }
    
    /// <summary>
    /// Orders types for smart truncation: clients first with their deps, then options, enums, models.
    /// </summary>
    private static List<TypeInfo> GetPrioritizedTypes(List<TypeInfo> allTypes, HashSet<string> allTypeNames)
    {
        // Get clients and their dependencies first
        var clients = allTypes.Where(t => t.IsClientType).ToList();
        var clientDeps = new HashSet<string>();
        foreach (var client in clients)
        {
            foreach (var dep in client.GetReferencedTypes(allTypeNames))
                clientDeps.Add(dep);
        }
        
        // Order: Clients → Client deps → Options → Exceptions → Enums → Models → Rest
        return allTypes
            .OrderBy(t =>
            {
                if (t.IsClientType) return 0;
                if (clientDeps.Contains(t.Name)) return 1;
                return t.TruncationPriority + 2;
            })
            .ThenBy(t => t.Name)
            .ToList();
    }
    
    private static string GetNamespace(TypeInfo type, ApiIndex index)
    {
        foreach (var ns in index.Namespaces)
            if (ns.Types.Contains(type))
                return ns.Name;
        return "";
    }
    
    private static string FormatTypes(List<TypeInfo> types, string namespaceName)
    {
        var sb = new StringBuilder();
        
        if (!string.IsNullOrEmpty(namespaceName))
        {
            sb.AppendLine($"namespace {namespaceName}");
            sb.AppendLine("{");
        }
        
        var indent = string.IsNullOrEmpty(namespaceName) ? "" : "    ";
        
        foreach (var type in types)
            FormatType(sb, type, indent);
        
        if (!string.IsNullOrEmpty(namespaceName))
        {
            sb.AppendLine("}");
            sb.AppendLine();
        }
        
        return sb.ToString();
    }
    
    private static void FormatType(StringBuilder sb, TypeInfo type, string indent)
    {
        // XML doc
        if (!string.IsNullOrEmpty(type.Doc))
        {
            sb.AppendLine($"{indent}/// <summary>{EscapeXml(type.Doc)}</summary>");
        }
        
        // Type declaration
        var inheritance = BuildInheritance(type);
        sb.Append($"{indent}public {type.Kind} {type.Name}");
        if (!string.IsNullOrEmpty(inheritance))
            sb.Append($" : {inheritance}");
        
        // Enum values
        if (type.Kind == "enum" && type.Values?.Count > 0)
        {
            sb.AppendLine($" {{ {string.Join(", ", type.Values)} }}");
            sb.AppendLine();
            return;
        }
        
        sb.AppendLine();
        sb.AppendLine($"{indent}{{");
        
        // Group members by kind once for efficient iteration (instead of 7x .Where() scans)
        var membersByKind = new Dictionary<string, List<MemberInfo>>();
        foreach (var m in type.Members ?? [])
        {
            var key = m.Kind ?? "other";
            if (!membersByKind.TryGetValue(key, out var list))
            {
                list = [];
                membersByKind[key] = list;
            }
            list.Add(m);
        }
        
        // Constants first
        if (membersByKind.TryGetValue("const", out var consts))
        {
            foreach (var m in consts)
                FormatMember(sb, m, indent + "    ");
        }
        
        // Static properties
        if (membersByKind.TryGetValue("property", out var properties))
        {
            foreach (var m in properties.Where(m => m.IsStatic == true))
                FormatMember(sb, m, indent + "    ");
        }
        
        // Constructors
        if (membersByKind.TryGetValue("ctor", out var ctors))
        {
            foreach (var m in ctors)
                FormatMember(sb, m, indent + "    ");
        }
        
        // Instance properties
        if (membersByKind.TryGetValue("property", out var props))
        {
            foreach (var m in props.Where(m => m.IsStatic != true))
                FormatMember(sb, m, indent + "    ");
        }
        
        // Indexers
        if (membersByKind.TryGetValue("indexer", out var indexers))
        {
            foreach (var m in indexers)
                FormatMember(sb, m, indent + "    ");
        }
        
        // Events
        if (membersByKind.TryGetValue("event", out var events))
        {
            foreach (var m in events)
                FormatMember(sb, m, indent + "    ");
        }
        
        // Methods
        if (membersByKind.TryGetValue("method", out var methods))
        {
            foreach (var m in methods)
                FormatMember(sb, m, indent + "    ");
        }
        
        sb.AppendLine($"{indent}}}");
        sb.AppendLine();
    }
    
    private static void FormatMember(StringBuilder sb, MemberInfo member, string indent)
    {
        // XML doc
        if (!string.IsNullOrEmpty(member.Doc))
            sb.AppendLine($"{indent}/// <summary>{EscapeXml(member.Doc)}</summary>");
        
        var modifiers = new List<string> { "public" };
        if (member.IsStatic == true) modifiers.Add("static");
        if (member.IsAsync == true && member.Kind == "method") modifiers.Add("async");
        
        var mods = string.Join(" ", modifiers);
        
        // Properties/indexers already have { get; } in signature - don't add semicolon
        var sig = member.Signature;
        var needsSemicolon = !sig.EndsWith("}");
        var suffix = needsSemicolon ? ";" : "";
        
        switch (member.Kind)
        {
            case "ctor":
                sb.AppendLine($"{indent}public {member.Name}{sig};");
                break;
            case "property":
            case "indexer":
                sb.AppendLine($"{indent}{mods} {sig}{suffix}");
                break;
            case "event":
                sb.AppendLine($"{indent}{mods} {sig};");
                break;
            case "method":
                sb.AppendLine($"{indent}{mods} {sig};");
                break;
            case "const":
                sb.AppendLine($"{indent}public {sig};");
                break;
        }
    }
    
    private static string BuildInheritance(TypeInfo type)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(type.Base))
            parts.Add(type.Base);
        if (type.Interfaces?.Count > 0)
            parts.AddRange(type.Interfaces);
        return string.Join(", ", parts);
    }
    
    private static string EscapeXml(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    
    /// <summary>
    /// Formats types with proper namespace wrapping.
    /// </summary>
    private static string FormatTypesWithNamespace(IEnumerable<TypeInfo> types, ApiIndex index)
    {
        var sb = new StringBuilder();
        var typesByNamespace = types.GroupBy(t => GetNamespace(t, index));
        
        foreach (var group in typesByNamespace)
        {
            var ns = group.Key;
            if (!string.IsNullOrEmpty(ns))
            {
                sb.AppendLine($"namespace {ns}");
                sb.AppendLine("{");
            }
            
            var indent = string.IsNullOrEmpty(ns) ? "" : "    ";
            foreach (var type in group)
                FormatType(sb, type, indent);
            
            if (!string.IsNullOrEmpty(ns))
            {
                sb.AppendLine("}");
                sb.AppendLine();
            }
        }
        
        return sb.ToString();
    }
}
