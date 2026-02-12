// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using ApiExtractor.Contracts;

namespace ApiExtractor.Python;

/// <summary>
/// Formats an ApiIndex as human-readable Python stub syntax.
/// Supports smart truncation that prioritizes clients and their dependencies.
/// </summary>
public static class PythonFormatter
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
            sb.AppendLine($"# ALREADY COVERED ({totalCovered} calls across {coverage.FileCount} files) - DO NOT DUPLICATE:");
            foreach (var (client, ops) in coveredByClient.OrderBy(kv => kv.Key))
            {
                sb.AppendLine($"#   {client}: {string.Join(", ", ops.Take(10))}{(ops.Count > 10 ? $" (+{ops.Count - 10} more)" : "")}");
            }
            sb.AppendLine();
        }

        // Section 2: Full signatures for types containing uncovered operations
        var uncoveredByClient = coverage.UncoveredOperations
            .GroupBy(op => op.ClientType)
            .ToDictionary(g => g.Key, g => g.Select(op => op.Operation).ToHashSet());

        if (uncoveredByClient.Count == 0)
        {
            sb.AppendLine("# All operations are covered in existing samples.");
            return sb.ToString();
        }

        sb.AppendLine($"# UNCOVERED API ({coverage.UncoveredOperations.Count} operations) - Generate samples for these:");
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
                sb.AppendLine($"# ... truncated ({classesWithUncovered.Count - includedClasses.Count} classes omitted)");
                break;
            }

            sb.Append(classContent);
            currentLength += classContent.Length;
            includedClasses.Add(cls.Name);

            // Include supporting dependency types referenced by uncovered methods
            var deps = filteredClass.GetReferencedTypes(
                allClasses.Select(c => c.Name).ToHashSet());
            foreach (var depName in deps)
            {
                if (includedClasses.Contains(depName))
                    continue;

                var depClass = allClasses.FirstOrDefault(c => c.Name == depName);
                if (depClass == null)
                    continue;

                var depContent = FormatClassToString(depClass);
                if (currentLength + depContent.Length > maxLength - 100)
                    break;

                sb.Append(depContent);
                currentLength += depContent.Length;
                includedClasses.Add(depName);
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
                    if (currentLength + depContent.Length > maxLength - 100)
                        break;

                    sb.Append(depContent);
                    currentLength += depContent.Length;
                    includedClasses.Add(cls.Name);
                }
            }
        }

        return sb.ToString();
    }

    private static string FormatClassToString(ClassInfo cls)
    {
        var sb = new StringBuilder();
        FormatClass(sb, cls);
        return sb.ToString();
    }

    /// <summary>
    /// Formats with smart truncation to fit within budget.
    /// Prioritizes: Clients → Their dependencies → Options → Models → Rest
    /// </summary>
    public static string Format(ApiIndex index, int maxLength)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# {index.Package} - Public API Surface");
        sb.AppendLine();

        // Build type lookup
        var allClasses = index.GetAllClasses().ToList();
        var allTypeNames = allClasses.Select(c => c.Name).ToHashSet();

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

        foreach (var module in index.Modules ?? [])
        {
            var moduleClasses = orderedClasses
                .Where(c => module.Classes?.Contains(c) ?? false)
                .ToList();

            if (moduleClasses.Count == 0 && (module.Functions?.Count ?? 0) == 0)
                continue;

            sb.AppendLine($"# Module: {module.Name}");
            sb.AppendLine();

            // Module-level functions (usually factory functions - include them)
            foreach (var func in module.Functions ?? [])
            {
                var funcContent = FormatFunctionToString(func, "");
                if (currentLength + funcContent.Length > maxLength - 100 && includedClasses.Count > 0)
                    break;
                sb.Append(funcContent);
                currentLength += funcContent.Length;
            }

            foreach (var cls in moduleClasses)
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

                var classContent = FormatClassesToString(classesToAdd);

                if (currentLength + classContent.Length > maxLength - 100 && includedClasses.Count > 0)
                {
                    sb.AppendLine($"# ... truncated ({allClasses.Count - includedClasses.Count} classes omitted)");
                    return sb.ToString();
                }

                sb.Append(classContent);
                currentLength += classContent.Length;

                foreach (var c in classesToAdd)
                    includedClasses.Add(c.Name);
            }

            sb.AppendLine();
        }

        // Add dependency types section
        if (index.Dependencies?.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"# {new string('=', 77)}");
            sb.AppendLine("# Dependency Types (from external packages)");
            sb.AppendLine($"# {new string('=', 77)}");
            sb.AppendLine();

            foreach (var dep in index.Dependencies)
            {
                sb.AppendLine($"# From: {dep.Package}");
                sb.AppendLine();

                foreach (var cls in dep.Classes ?? [])
                    FormatClass(sb, cls);

                foreach (var func in dep.Functions ?? [])
                    FormatFunction(sb, func, "");
            }
        }

        return sb.ToString();
    }

    private static string FormatClassesToString(List<ClassInfo> classes)
    {
        var sb = new StringBuilder();
        foreach (var cls in classes)
            FormatClass(sb, cls);
        return sb.ToString();
    }

    private static string FormatFunctionToString(FunctionInfo func, string indent)
    {
        var sb = new StringBuilder();
        FormatFunction(sb, func, indent);
        return sb.ToString();
    }

    private static void FormatClass(StringBuilder sb, ClassInfo cls)
    {
        var baseClass = !string.IsNullOrEmpty(cls.Base) ? $"({cls.Base})" : "";
        sb.AppendLine($"class {cls.Name}{baseClass}:");

        if (!string.IsNullOrEmpty(cls.Doc))
            sb.AppendLine($"    \"\"\"{cls.Doc}\"\"\"");

        var hasMembers = false;

        // Properties
        foreach (var prop in cls.Properties ?? [])
        {
            if (!string.IsNullOrEmpty(prop.Doc))
                sb.AppendLine($"        \"\"\"{prop.Doc}\"\"\"");

            var typeHint = !string.IsNullOrEmpty(prop.Type) ? $": {prop.Type}" : "";
            sb.AppendLine($"    {prop.Name}{typeHint}");
            hasMembers = true;
        }

        // Methods
        foreach (var method in cls.Methods ?? [])
        {
            FormatMethod(sb, method, "    ");
            hasMembers = true;
        }

        if (!hasMembers)
            sb.AppendLine("    ...");

        sb.AppendLine();
    }

    private static void FormatMethod(StringBuilder sb, MethodInfo method, string indent)
    {
        List<string> decorators = [];
        if (method.IsClassMethod == true) decorators.Add("@classmethod");
        if (method.IsStaticMethod == true) decorators.Add("@staticmethod");

        foreach (var dec in decorators)
            sb.AppendLine($"{indent}{dec}");

        var asyncPrefix = method.IsAsync == true ? "async " : "";
        var retAnnotation = !string.IsNullOrEmpty(method.Ret) ? $" -> {method.Ret}" : "";
        sb.AppendLine($"{indent}{asyncPrefix}def {method.Name}({method.Signature}){retAnnotation}: ...");

        if (!string.IsNullOrEmpty(method.Doc))
            sb.AppendLine($"{indent}    \"\"\"{method.Doc}\"\"\"");
    }

    private static void FormatFunction(StringBuilder sb, FunctionInfo func, string indent)
    {
        var asyncPrefix = func.IsAsync == true ? "async " : "";
        var retAnnotation = !string.IsNullOrEmpty(func.Ret) ? $" -> {func.Ret}" : "";
        sb.AppendLine($"{indent}{asyncPrefix}def {func.Name}({func.Signature}){retAnnotation}: ...");

        if (!string.IsNullOrEmpty(func.Doc))
            sb.AppendLine($"{indent}    \"\"\"{func.Doc}\"\"\"");
        sb.AppendLine();
    }
}
