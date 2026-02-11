// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Frozen;
using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;

namespace ApiExtractor.DotNet;

/// <summary>
/// Entry point detection: resolves namespace entry points from .csproj configuration.
/// </summary>
public partial class CSharpApiExtractor
{
    /// <summary>
    /// Resolves entry point namespaces from project configuration.
    /// Entry points in .NET are determined by:
    /// 1. RootNamespace from .csproj
    /// 2. PackageId from .csproj
    /// 3. Inferred from project directory name
    /// Returns a FrozenSet for guaranteed thread-safe concurrent reads.
    /// </summary>
    private static FrozenSet<string> ResolveEntryPointNamespaces(string rootPath)
    {
        var entryPoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var csproj = Directory.EnumerateFiles(rootPath, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault()
                  ?? Directory.EnumerateFiles(rootPath, "*.csproj", SearchOption.AllDirectories).FirstOrDefault();

        if (csproj != null)
        {
            try
            {
                var doc = LoadXmlSecure(csproj);

                var rootNs = ExtractCsprojProperty(doc, "RootNamespace");
                if (!string.IsNullOrEmpty(rootNs))
                    entryPoints.Add(rootNs);

                var packageId = ExtractCsprojProperty(doc, "PackageId");
                if (!string.IsNullOrEmpty(packageId))
                    entryPoints.Add(packageId);

                var assemblyName = ExtractCsprojProperty(doc, "AssemblyName");
                if (!string.IsNullOrEmpty(assemblyName))
                    entryPoints.Add(assemblyName);
            }
            catch (Exception ex) when (ex is XmlException or IOException)
            {
                Trace.TraceWarning("Failed to parse csproj '{0}': {1}", csproj, ex.Message);
            }

            // Fallback to project file name
            var projectName = Path.GetFileNameWithoutExtension(csproj);
            if (!string.IsNullOrEmpty(projectName))
            {
                entryPoints.Add(projectName);
            }
        }
        else
        {
            // No csproj, use directory name as namespace hint
            var dirName = Path.GetFileName(rootPath);
            if (!string.IsNullOrEmpty(dirName))
            {
                entryPoints.Add(dirName);
            }
        }

        return entryPoints.ToFrozenSet();
    }

    /// <summary>
    /// Extracts a property value from a parsed .csproj XDocument.
    /// </summary>
    private static string? ExtractCsprojProperty(XDocument doc, string propertyName)
    {
        var value = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == propertyName)?.Value?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// Checks if a namespace is an entry point namespace.
    /// </summary>
    private static bool IsEntryPointNamespace(string ns, FrozenSet<string> entryPointNamespaces)
    {
        if (entryPointNamespaces.Count == 0)
        {
            return false;
        }

        foreach (var entryNs in entryPointNamespaces)
        {
            // Exact match or the namespace equals the entry point
            if (string.Equals(ns, entryNs, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Namespace is a direct child of entry point (e.g., "MyPkg.Models" when entry is "MyPkg")
            // But exclude implementation/internal namespaces
            if (ns.StartsWith(entryNs + ".", StringComparison.OrdinalIgnoreCase))
            {
                var suffix = ns[(entryNs.Length + 1)..].ToLowerInvariant();
                if (!suffix.Contains(".internal", StringComparison.Ordinal) && !suffix.Contains(".implementation", StringComparison.Ordinal))
                {
                    // Direct children of entry namespace are entry points
                    // But deeper nested ones are typically supporting types
                    var depth = suffix.Count(c => c == '.');
                    return depth == 0;
                }
            }
        }

        return false;
    }
}
