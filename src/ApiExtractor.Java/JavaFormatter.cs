// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using ApiExtractor.Contracts;

namespace ApiExtractor.Java;

/// <summary>
/// Formats extracted Java API as Java stub syntax.
/// Supports smart truncation that prioritizes clients and their dependencies.
/// </summary>
public static class JavaFormatter
{
    /// <summary>Formats the full API surface.</summary>
    public static string Format(ApiIndex api) => Format(api, int.MaxValue);

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

        var allClasses = index.GetAllClasses().ToList();
        var classesWithUncovered = allClasses.Where(c => uncoveredByClient.ContainsKey(c.Name)).ToList();

        HashSet<string> includedClasses = [];
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
        var sb = new StringBuilder();
        FormatType(sb, cls, "class");
        return sb.ToString();
    }

    /// <summary>
    /// Formats with smart truncation to fit within budget.
    /// Prioritizes: Clients → Their dependencies → Builders → Options → Models → Rest
    /// </summary>
    public static string Format(ApiIndex api, int maxLength)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"// {api.Package} - Public API Surface");
        sb.AppendLine();

        // Build type lookup
        var allClasses = api.GetAllClasses().ToList();
        var allTypeNames = allClasses.Select(c => c.Name.Split('<')[0]).ToHashSet();

        // Get client dependencies first
        var clients = allClasses.Where(c => c.IsClientType).ToList();
        HashSet<string> clientDeps = [];
        foreach (var client in clients)
            foreach (var dep in client.GetReferencedTypes(allTypeNames))
                clientDeps.Add(dep);

        // Prioritize classes
        var orderedClasses = allClasses
            .OrderBy(c =>
            {
                if (c.IsClientType) return 0;
                if (clientDeps.Contains(c.Name)) return 1;
                return c.TruncationPriority + 2;
            })
            .ThenBy(c => c.Name)
            .ToList();

        HashSet<string> includedClasses = [];
        var currentLength = sb.Length;

        foreach (var pkg in api.Packages)
        {
            var pkgClasses = orderedClasses
                .Where(c => (pkg.Classes?.Contains(c) == true) || (pkg.Interfaces?.Contains(c) == true))
                .ToList();

            if (pkgClasses.Count == 0 && (pkg.Enums?.Count ?? 0) == 0)
                continue;

            sb.AppendLine($"package {pkg.Name};");
            sb.AppendLine();

            // Enums first (usually small)
            if (pkg.Enums != null)
                FormatEnums(sb, pkg.Enums);

            foreach (var cls in pkgClasses)
            {
                if (includedClasses.Contains(cls.Name))
                    continue;

                // Include class + dependencies
                List<ClassInfo> classesToAdd = [cls];
                var deps = cls.GetReferencedTypes(allTypeNames);
                foreach (var depName in deps)
                {
                    if (!includedClasses.Contains(depName))
                    {
                        var depClass = allClasses.FirstOrDefault(c => c.Name == depName);
                        if (depClass != null)
                            classesToAdd.Add(depClass);
                    }
                }

                var classContent = FormatTypesToString(classesToAdd, pkg.Interfaces?.Any(i => classesToAdd.Contains(i)) == true);

                if (currentLength + classContent.Length > maxLength - 100 && includedClasses.Count > 0)
                {
                    sb.AppendLine($"// ... truncated ({allClasses.Count - includedClasses.Count} classes omitted)");
                    return sb.ToString();
                }

                sb.Append(classContent);
                currentLength += classContent.Length;

                foreach (var c in classesToAdd)
                    includedClasses.Add(c.Name);
            }
        }

        // Add dependency types section
        if (api.Dependencies?.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"// {new string('=', 77)}");
            sb.AppendLine("// Dependency Types (from external packages)");
            sb.AppendLine($"// {new string('=', 77)}");
            sb.AppendLine();

            foreach (var dep in api.Dependencies)
            {
                if (sb.Length >= maxLength) break;

                sb.AppendLine($"// From: {dep.Package}");
                sb.AppendLine();

                foreach (var iface in dep.Interfaces ?? [])
                {
                    if (sb.Length >= maxLength) break;
                    FormatType(sb, iface, "interface");
                }

                foreach (var cls in dep.Classes ?? [])
                {
                    if (sb.Length >= maxLength) break;
                    FormatType(sb, cls, "class");
                }

                foreach (var e in dep.Enums ?? [])
                {
                    if (sb.Length >= maxLength) break;
                    FormatEnums(sb, [e]);
                }
            }
        }

        return sb.ToString();
    }

    private static string FormatTypesToString(List<ClassInfo> types, bool isInterface)
    {
        var sb = new StringBuilder();
        var keyword = isInterface ? "interface" : "class";
        foreach (var type in types)
            FormatType(sb, type, keyword);
        return sb.ToString();
    }

    private static void FormatType(StringBuilder sb, ClassInfo type, string keyword)
    {
        if (!string.IsNullOrEmpty(type.Doc))
            sb.AppendLine($"/** {type.Doc} */");

        var mods = type.Modifiers != null ? string.Join(" ", type.Modifiers) + " " : "";
        var typeParams = !string.IsNullOrEmpty(type.TypeParams) ? $"<{type.TypeParams}>" : "";
        var ext = !string.IsNullOrEmpty(type.Extends) ? $" extends {type.Extends}" : "";
        var impl = type.Implements?.Count > 0 ? $" implements {string.Join(", ", type.Implements)}" : "";

        sb.AppendLine($"{mods}{keyword} {type.Name}{typeParams}{ext}{impl} {{");

        // Fields
        if (type.Fields != null)
        {
            foreach (var f in type.Fields)
            {
                var fm = f.Modifiers != null ? string.Join(" ", f.Modifiers) + " " : "";
                var val = !string.IsNullOrEmpty(f.Value) ? $" = {f.Value}" : "";
                sb.AppendLine($"    {fm}{f.Type} {f.Name}{val};");
            }
        }

        // Constructors
        if (type.Constructors != null)
        {
            foreach (var c in type.Constructors)
                FormatMethod(sb, type.Name, c, isCtor: true);
        }

        // Methods
        if (type.Methods != null)
        {
            foreach (var m in type.Methods)
                FormatMethod(sb, m.Name, m, isCtor: false);
        }

        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void FormatEnums(StringBuilder sb, IReadOnlyList<EnumInfo> enums)
    {
        foreach (var e in enums)
        {
            if (!string.IsNullOrEmpty(e.Doc))
                sb.AppendLine($"/** {e.Doc} */");

            sb.AppendLine($"public enum {e.Name} {{");

            if (e.Values?.Count > 0)
                sb.AppendLine($"    {string.Join(", ", e.Values)};");

            if (e.Methods != null)
            {
                foreach (var m in e.Methods)
                    FormatMethod(sb, m.Name, m, isCtor: false);
            }

            sb.AppendLine("}");
            sb.AppendLine();
        }
    }

    private static void FormatMethod(StringBuilder sb, string name, MethodInfo m, bool isCtor)
    {
        if (!string.IsNullOrEmpty(m.Doc))
            sb.AppendLine($"    /** {m.Doc} */");

        var mods = m.Modifiers != null ? string.Join(" ", m.Modifiers) + " " : "";
        var typeParams = !string.IsNullOrEmpty(m.TypeParams) ? $"<{m.TypeParams}> " : "";
        var ret = !isCtor && !string.IsNullOrEmpty(m.Ret) ? $"{m.Ret} " : "";
        var throws = m.Throws?.Count > 0 ? $" throws {string.Join(", ", m.Throws)}" : "";

        sb.AppendLine($"    {mods}{typeParams}{ret}{name}({m.Sig}){throws};");
    }
}
