// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using ApiExtractor.Contracts;

namespace ApiExtractor.Go;

/// <summary>
/// Formats an ApiIndex as human-readable Go stub syntax.
/// Supports smart truncation that prioritizes clients and their dependencies.
/// </summary>
public static class GoFormatter
{
    /// <summary>Formats the full API surface.</summary>
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

        var allStructs = index.GetAllStructs().ToList();
        var structsWithUncovered = allStructs.Where(s => uncoveredByClient.ContainsKey(s.Name)).ToList();

        // Build set of all type names for dependency tracking
        var allTypeNames = allStructs.Select(s => s.Name).ToHashSet();
        var structsByName = allStructs.ToDictionary(s => s.Name);

        HashSet<string> includedStructs = [];

        foreach (var st in structsWithUncovered)
        {
            if (includedStructs.Contains(st.Name))
                continue;

            // Filter to show only uncovered methods for client structs
            var filteredStruct = st;
            if (uncoveredByClient.TryGetValue(st.Name, out var uncoveredOps))
            {
                filteredStruct = st with
                {
                    Methods = st.Methods?
                        .Where(m => uncoveredOps.Contains(m.Name))
                        .ToList() ?? []
                };
            }

            var structContent = FormatStructToString(filteredStruct);

            if (sb.Length + structContent.Length > maxLength - 100 && includedStructs.Count > 0)
            {
                sb.AppendLine($"// ... truncated ({structsWithUncovered.Count - includedStructs.Count} structs omitted)");
                break;
            }

            sb.Append(structContent);
            includedStructs.Add(st.Name);

            // Include supporting model/option types referenced by uncovered operations
            var deps = filteredStruct.GetReferencedTypes(allTypeNames);
            foreach (var depName in deps)
            {
                if (!includedStructs.Contains(depName) && structsByName.TryGetValue(depName, out var depStruct))
                {
                    var depContent = FormatStructToString(depStruct);
                    if (sb.Length + depContent.Length > maxLength - 100)
                        break;
                    sb.Append(depContent);
                    includedStructs.Add(depName);
                }
            }
        }

        return sb.ToString();
    }

    private static string FormatStructToString(StructApi st)
    {
        var sb = new StringBuilder();
        FormatStruct(sb, st);
        return sb.ToString();
    }

    /// <summary>
    /// Formats with smart truncation to fit within budget.
    /// Prioritizes: Clients → Their dependencies → Options → Models → Rest
    /// </summary>
    public static string Format(ApiIndex index, int maxLength)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"// {index.Package} - Public API Surface");
        sb.AppendLine();

        // Build type lookup
        var allStructs = index.GetAllStructs().ToList();
        var allTypeNames = allStructs.Select(s => s.Name).ToHashSet();

        // Pre-build dictionary for O(1) lookups instead of O(n) FirstOrDefault
        var structsByName = allStructs.ToDictionary(s => s.Name);

        // Get client dependencies first
        var clients = allStructs.Where(s => s.IsClientType).ToList();
        HashSet<string> clientDeps = [];
        foreach (var client in clients)
            foreach (var dep in client.GetReferencedTypes(allTypeNames))
                clientDeps.Add(dep);

        // Prioritize structs
        var orderedStructs = allStructs
            .OrderBy(s =>
            {
                if (s.IsClientType) return 0;
                if (clientDeps.Contains(s.Name)) return 1;
                return s.TruncationPriority + 2;
            })
            .ThenBy(s => s.Name)
            .ToList();

        HashSet<string> includedStructs = [];
        var currentLength = sb.Length;

        foreach (var pkg in index.Packages ?? [])
        {
            var pkgStructs = orderedStructs
                .Where(s => pkg.Structs?.Contains(s) ?? false)
                .ToList();

            if (pkgStructs.Count == 0 &&
                (pkg.Functions?.Count ?? 0) == 0 &&
                (pkg.Interfaces?.Count ?? 0) == 0)
                continue;

            sb.AppendLine($"// Package: {pkg.Name}");
            if (!string.IsNullOrEmpty(pkg.Doc))
                sb.AppendLine($"// {pkg.Doc}");
            sb.AppendLine();

            // Type aliases (usually small)
            foreach (var t in pkg.Types ?? [])
            {
                if (!string.IsNullOrEmpty(t.Doc))
                    sb.AppendLine($"// {t.Doc}");
                sb.AppendLine($"type {t.Name} = {t.Type}");
                sb.AppendLine();
            }

            // Constants (usually small)
            if (pkg.Constants?.Count > 0)
            {
                sb.AppendLine("const (");
                foreach (var c in pkg.Constants.Take(20)) // Limit constants
                {
                    if (!string.IsNullOrEmpty(c.Doc))
                        sb.AppendLine($"    // {c.Doc}");
                    var value = !string.IsNullOrEmpty(c.Value) ? $" = {c.Value}" : "";
                    var type = !string.IsNullOrEmpty(c.Type) ? $" {c.Type}" : "";
                    sb.AppendLine($"    {c.Name}{type}{value}");
                }
                if (pkg.Constants.Count > 20)
                    sb.AppendLine($"    // ... {pkg.Constants.Count - 20} more constants");
                sb.AppendLine(")");
                sb.AppendLine();
            }

            foreach (var iface in pkg.Interfaces ?? [])
            {
                if (!string.IsNullOrEmpty(iface.Doc))
                    sb.AppendLine($"// {iface.Doc}");
                sb.AppendLine($"type {iface.Name} interface {{");
                foreach (var embed in iface.Embeds ?? [])
                {
                    sb.AppendLine($"    {embed}");
                }
                foreach (var m in iface.Methods ?? [])
                {
                    var ret = !string.IsNullOrEmpty(m.Ret) ? $" {m.Ret}" : "";
                    sb.AppendLine($"    {m.Name}({m.Sig}){ret}");
                }
                sb.AppendLine("}");
                sb.AppendLine();
            }

            // Structs by priority
            foreach (var s in pkgStructs)
            {
                if (includedStructs.Contains(s.Name))
                    continue;

                // Include struct + dependencies
                List<StructApi> structsToAdd = [s];
                var deps = s.GetReferencedTypes(allTypeNames);
                foreach (var depName in deps)
                {
                    if (!includedStructs.Contains(depName) && structsByName.TryGetValue(depName, out var depStruct))
                    {
                        structsToAdd.Add(depStruct);
                    }
                }

                var structContent = FormatStructsToString(structsToAdd);

                if (currentLength + structContent.Length > maxLength - 100 && includedStructs.Count > 0)
                {
                    sb.AppendLine($"// ... truncated ({allStructs.Count - includedStructs.Count} structs omitted)");
                    return sb.ToString();
                }

                sb.Append(structContent);
                currentLength += structContent.Length;

                foreach (var st in structsToAdd)
                    includedStructs.Add(st.Name);
            }

            // Top-level functions (factory functions like NewClient)
            foreach (var f in pkg.Functions ?? [])
            {
                if (!string.IsNullOrEmpty(f.Doc))
                    sb.AppendLine($"// {f.Doc}");
                var ret = !string.IsNullOrEmpty(f.Ret) ? $" {f.Ret}" : "";
                sb.AppendLine($"func {f.Name}({f.Sig}){ret}");
                sb.AppendLine();
            }
        }

        // Add dependency types section
        if (index.Dependencies?.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"// {new string('=', 77)}");
            sb.AppendLine("// Dependency Types (from external modules)");
            sb.AppendLine($"// {new string('=', 77)}");
            sb.AppendLine();

            foreach (var dep in index.Dependencies)
            {
                if (sb.Length >= maxLength) break;

                sb.AppendLine($"// From: {dep.Package}");
                sb.AppendLine();

                foreach (var iface in dep.Interfaces ?? [])
                {
                    if (sb.Length >= maxLength) break;
                    if (!string.IsNullOrEmpty(iface.Doc))
                        sb.AppendLine($"// {iface.Doc}");
                    sb.AppendLine($"type {iface.Name} interface {{");
                    foreach (var embed in iface.Embeds ?? [])
                    {
                        sb.AppendLine($"    {embed}");
                    }
                    foreach (var m in iface.Methods ?? [])
                    {
                        var ret = !string.IsNullOrEmpty(m.Ret) ? $" {m.Ret}" : "";
                        sb.AppendLine($"    {m.Name}({m.Sig}){ret}");
                    }
                    sb.AppendLine("}");
                    sb.AppendLine();
                }

                foreach (var s in dep.Structs ?? [])
                {
                    if (sb.Length >= maxLength) break;
                    FormatStruct(sb, s);
                }

                foreach (var t in dep.Types ?? [])
                {
                    if (sb.Length >= maxLength) break;
                    if (!string.IsNullOrEmpty(t.Doc))
                        sb.AppendLine($"// {t.Doc}");
                    sb.AppendLine($"type {t.Name} = {t.Type}");
                    sb.AppendLine();
                }
            }
        }

        return sb.ToString();
    }

    private static string FormatStructsToString(List<StructApi> structs)
    {
        var sb = new StringBuilder();
        foreach (var s in structs)
            FormatStruct(sb, s);
        return sb.ToString();
    }

    private static void FormatStruct(StringBuilder sb, StructApi s)
    {
        if (!string.IsNullOrEmpty(s.Doc))
            sb.AppendLine($"// {s.Doc}");
        sb.AppendLine($"type {s.Name} struct {{");

        // Embedded types (Go composition)
        foreach (var embed in s.Embeds ?? [])
        {
            sb.AppendLine($"    {embed}");
        }

        foreach (var f in s.Fields ?? [])
        {
            var tag = !string.IsNullOrEmpty(f.Tag) ? $" {f.Tag}" : "";
            sb.AppendLine($"    {f.Name} {f.Type}{tag}");
        }
        sb.AppendLine("}");

        // Methods
        foreach (var m in s.Methods ?? [])
        {
            var recv = !string.IsNullOrEmpty(m.Receiver) ? m.Receiver : $"*{s.Name}";
            var ret = !string.IsNullOrEmpty(m.Ret) ? $" {m.Ret}" : "";
            sb.AppendLine($"func ({recv}) {m.Name}({m.Sig}){ret}");
        }
        sb.AppendLine();
    }
}
