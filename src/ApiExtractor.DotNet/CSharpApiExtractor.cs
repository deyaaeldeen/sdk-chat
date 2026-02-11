// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using ApiExtractor.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ApiExtractor.DotNet;

/// <summary>
/// Extracts public API surface from C# source files using Roslyn.
/// Merges partial classes and only extracts public members.
/// </summary>
public class CSharpApiExtractor : IApiExtractor<ApiIndex>
{
    /// <summary>
    /// Maximum number of syntax trees to process per batch during semantic analysis.
    /// This limits memory consumption for very large repos (e.g., Azure SDK with 10K+ files).
    /// Semantic models are released between batches to allow garbage collection.
    /// </summary>
    internal const int MaxSyntaxTreesPerBatch = 500;

    /// <summary>
    /// Cached runtime metadata references. These are the same for the lifetime of the process
    /// (same AppContext.BaseDirectory), so we load them once.
    /// </summary>
    private static readonly Lazy<IReadOnlyList<MetadataReference>> s_runtimeReferences = new(LoadRuntimeReferences);

    /// <inheritdoc />
    public string Language => "csharp";

    /// <inheritdoc />
    public bool IsAvailable() => true; // Roslyn is embedded, always available

    /// <inheritdoc />
    public string? UnavailableReason => null;

    /// <inheritdoc />
    public string ToJson(ApiIndex index, bool pretty = false)
        => pretty
            ? JsonSerializer.Serialize(index, JsonContext.Indented.ApiIndex)
            : JsonSerializer.Serialize(index, JsonContext.Default.ApiIndex);

    /// <inheritdoc />
    public string ToStubs(ApiIndex index) => CSharpFormatter.Format(index);

    /// <inheritdoc />
    async Task<ExtractorResult<ApiIndex>> IApiExtractor<ApiIndex>.ExtractAsync(string rootPath, CancellationToken ct)
    {
        try
        {
            var result = await ExtractAsync(rootPath, ct).ConfigureAwait(false);
            return ExtractorResult<ApiIndex>.CreateSuccess(result);
        }
        catch (Exception ex)
        {
            return ExtractorResult<ApiIndex>.CreateFailure($"{ex.Message}\n{ex.StackTrace}");
        }
    }

    public async Task<ApiIndex> ExtractAsync(string rootPath, CancellationToken ct = default)
    {
        rootPath = ProcessSandbox.ValidateRootPath(rootPath);
        var files = Directory.EnumerateFiles(rootPath, "*.cs", SearchOption.AllDirectories)
            .Where(f =>
            {
                // Filter out build output, version control, and common non-source directories.
                // ContainsSegment does boundary-aware matching (e.g. "bin" won't match "binary")
                // so this is safe against partial-name collisions.
                var relativePath = Path.GetRelativePath(rootPath, f);
                return !ContainsSegment(relativePath, "obj")
                    && !ContainsSegment(relativePath, "bin")
                    && !ContainsSegment(relativePath, ".git")
                    && !ContainsSegment(relativePath, ".vs")
                    && !ContainsSegment(relativePath, "node_modules");
            })
            .ToList();

        var typeMap = new ConcurrentDictionary<string, MergedType>();

        // Resolve entry point namespaces from project configuration
        var entryPointNamespaces = ResolveEntryPointNamespaces(rootPath);

        // Parse all files into syntax trees (parallel)
        var syntaxTrees = new ConcurrentBag<SyntaxTree>();

        await Parallel.ForEachAsync(
            files,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8),
                CancellationToken = ct
            },
            async (file, token) =>
            {
                var code = await File.ReadAllTextAsync(file, token).ConfigureAwait(false);
                var tree = CSharpSyntaxTree.ParseText(code, path: file, cancellationToken: token);
                syntaxTrees.Add(tree);

                var root = await tree.GetRootAsync(token).ConfigureAwait(false);
                ExtractFromRoot(root, typeMap, entryPointNamespaces);
            }).ConfigureAwait(false);

        // Post-process: classify base types using locally-parsed type information
        // instead of relying on naming conventions.
        ClassifyBaseTypes(typeMap);

        var packageName = DetectPackageName(rootPath);

        var namespaces = typeMap.Values
            .GroupBy(t => t.Namespace)
            .OrderBy(g => g.Key)
            .Select(g => new NamespaceInfo
            {
                Name = g.Key,
                Types = g.OrderBy(t => t.Name).Select(t => t.ToTypeInfo()).ToList()
            })
            .ToList();

        // Resolve transitive dependencies using semantic analysis
        // Snapshot ConcurrentBag to List once; ResolveTransitiveDependencies uses it directly
        var treeList = syntaxTrees.ToList();
        var dependencies = ResolveTransitiveDependencies(rootPath, treeList, namespaces, ct);

        return new ApiIndex { Package = packageName, Namespaces = namespaces, Dependencies = dependencies };
    }

    #region Transitive Dependency Resolution (Semantic Analysis)

    /// <summary>
    /// .NET system assemblies that should be excluded from dependencies.
    /// These are part of the runtime/BCL and not external packages.
    /// Uses FrozenSet for zero-cost concurrent reads.
    /// </summary>
    private static readonly FrozenSet<string> SystemAssemblyPrefixes = new[]
    {
        "System", "mscorlib", "netstandard", "Microsoft.CSharp",
        "Microsoft.VisualBasic", "Microsoft.Win32", "WindowsBase",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Checks if an assembly is a system/runtime assembly (not an external dependency).
    /// </summary>
    private static bool IsSystemAssembly(string? assemblyName)
    {
        if (string.IsNullOrEmpty(assemblyName)) return true;

        // Exact match via FrozenSet O(1) lookup
        if (SystemAssemblyPrefixes.Contains(assemblyName)) return true;

        // Prefix match: check if assemblyName starts with any known prefix + "."
        // Span-based comparison avoids string concatenation allocation on every iteration
        // in the hot semantic-analysis path
        var nameSpan = assemblyName.AsSpan();
        foreach (var prefix in SystemAssemblyPrefixes)
        {
            if (nameSpan.Length > prefix.Length &&
                nameSpan[prefix.Length] == '.' &&
                nameSpan.StartsWith(prefix.AsSpan(), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Resolves transitive dependencies using Roslyn semantic analysis.
    /// Creates a compilation with NuGet package references to properly resolve external types.
    /// </summary>
    private static IReadOnlyList<DependencyInfo>? ResolveTransitiveDependencies(
        string rootPath,
        IReadOnlyList<SyntaxTree> syntaxTrees,
        IReadOnlyList<NamespaceInfo> namespaces,
        CancellationToken ct)
    {
        if (syntaxTrees.Count == 0) return null;

        // Collect all defined type names (to exclude from dependencies)
        var definedTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ns in namespaces)
            foreach (var type in ns.Types)
            {
                definedTypes.Add(type.Name.Split('<')[0]);
                // Also add fully qualified name
                if (!string.IsNullOrEmpty(ns.Name))
                {
                    definedTypes.Add($"{ns.Name}.{type.Name.Split('<')[0]}");
                }
            }

        // Load metadata references from NuGet packages
        var references = LoadMetadataReferences(rootPath);

        // Create compilation with all syntax trees and references
        var compilation = CSharpCompilation.Create(
            "ApiExtraction",
            syntaxTrees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(NullableContextOptions.Enable));

        // Collect external type symbols from all public API surfaces
        var externalTypes = new Dictionary<string, (ITypeSymbol Symbol, string AssemblyName)>(StringComparer.Ordinal);

        // Process syntax trees in batches to limit memory consumption
        // Roslyn semantic models can consume significant memory for large repos
        // Direct index-based loop avoids LINQ Skip/Take iterator allocations per batch
        var batchCount = (syntaxTrees.Count + MaxSyntaxTreesPerBatch - 1) / MaxSyntaxTreesPerBatch;

        for (int batchIndex = 0; batchIndex < batchCount; batchIndex++)
        {
            ct.ThrowIfCancellationRequested();

            var batchStart = batchIndex * MaxSyntaxTreesPerBatch;
            var batchEnd = Math.Min(batchStart + MaxSyntaxTreesPerBatch, syntaxTrees.Count);

            for (int i = batchStart; i < batchEnd; i++)
            {
                ct.ThrowIfCancellationRequested();
                var tree = syntaxTrees[i];

                var semanticModel = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot(ct);

                // Collect type references from all type declarations
                foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    if (!typeDecl.Modifiers.Any(SyntaxKind.PublicKeyword)) continue;

                    // Base types and interfaces
                    if (typeDecl.BaseList != null)
                    {
                        foreach (var baseType in typeDecl.BaseList.Types)
                        {
                            CollectExternalTypeSymbol(baseType.Type, semanticModel, definedTypes, externalTypes);
                        }
                    }

                    // Members
                    foreach (var member in typeDecl.Members)
                    {
                        if (!member.Modifiers.Any(SyntaxKind.PublicKeyword)) continue;

                        CollectTypesFromMember(member, semanticModel, definedTypes, externalTypes);
                    }
                }
            }
        }

        if (externalTypes.Count == 0) return null;

        // Group by assembly (package) name
        var byPackage = externalTypes
            .GroupBy(kv => kv.Value.AssemblyName, StringComparer.Ordinal)
            .Where(g => !IsSystemAssembly(g.Key))
            .OrderBy(g => g.Key)
            .Select(g => new DependencyInfo
            {
                Package = g.Key,
                Types = g.Select(kv => new TypeInfo
                {
                    Name = kv.Key,
                    Kind = GetTypeKindFromSymbol(kv.Value.Symbol)
                }).OrderBy(t => t.Name).ToList()
            })
            .ToList();

        return byPackage.Count > 0 ? byPackage : null;
    }

    /// <summary>
    /// Collects type symbols from a member declaration (method, property, etc.)
    /// </summary>
    private static void CollectTypesFromMember(
        MemberDeclarationSyntax member,
        SemanticModel semanticModel,
        HashSet<string> definedTypes,
        Dictionary<string, (ITypeSymbol, string)> externalTypes)
    {
        switch (member)
        {
            case MethodDeclarationSyntax method:
                CollectExternalTypeSymbol(method.ReturnType, semanticModel, definedTypes, externalTypes);
                foreach (var param in method.ParameterList.Parameters)
                {
                    if (param.Type != null)
                        CollectExternalTypeSymbol(param.Type, semanticModel, definedTypes, externalTypes);
                }
                // Collect from type constraints
                foreach (var constraint in method.ConstraintClauses)
                    foreach (var typeConstraint in constraint.Constraints.OfType<TypeConstraintSyntax>())
                    {
                        CollectExternalTypeSymbol(typeConstraint.Type, semanticModel, definedTypes, externalTypes);
                    }
                break;

            case PropertyDeclarationSyntax prop:
                CollectExternalTypeSymbol(prop.Type, semanticModel, definedTypes, externalTypes);
                break;

            case IndexerDeclarationSyntax indexer:
                CollectExternalTypeSymbol(indexer.Type, semanticModel, definedTypes, externalTypes);
                foreach (var param in indexer.ParameterList.Parameters)
                {
                    if (param.Type != null)
                        CollectExternalTypeSymbol(param.Type, semanticModel, definedTypes, externalTypes);
                }
                break;

            case EventDeclarationSyntax evt:
                CollectExternalTypeSymbol(evt.Type, semanticModel, definedTypes, externalTypes);
                break;

            case FieldDeclarationSyntax field:
                CollectExternalTypeSymbol(field.Declaration.Type, semanticModel, definedTypes, externalTypes);
                break;

            case ConstructorDeclarationSyntax ctor:
                foreach (var param in ctor.ParameterList.Parameters)
                {
                    if (param.Type != null)
                        CollectExternalTypeSymbol(param.Type, semanticModel, definedTypes, externalTypes);
                }
                break;
        }
    }

    /// <summary>
    /// Collects an external type symbol if it's from an external assembly.
    /// Recursively collects generic type arguments.
    /// </summary>
    private static void CollectExternalTypeSymbol(
        TypeSyntax typeSyntax,
        SemanticModel semanticModel,
        HashSet<string> definedTypes,
        Dictionary<string, (ITypeSymbol, string)> externalTypes)
    {
        var typeInfo = semanticModel.GetTypeInfo(typeSyntax);
        var typeSymbol = typeInfo.Type;

        if (typeSymbol == null) return;

        CollectExternalTypeSymbolRecursive(typeSymbol, definedTypes, externalTypes);
    }

    /// <summary>
    /// Recursively collects external type symbols, including generic type arguments.
    /// </summary>
    private static void CollectExternalTypeSymbolRecursive(
        ITypeSymbol typeSymbol,
        HashSet<string> definedTypes,
        Dictionary<string, (ITypeSymbol, string)> externalTypes)
    {
        if (typeSymbol is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            var underlyingType = nullable.TypeArguments.FirstOrDefault();
            if (underlyingType != null)
                CollectExternalTypeSymbolRecursive(underlyingType, definedTypes, externalTypes);
            return;
        }

        if (typeSymbol is IArrayTypeSymbol arrayType)
        {
            CollectExternalTypeSymbolRecursive(arrayType.ElementType, definedTypes, externalTypes);
            return;
        }

        // Skip type parameters (generics)
        if (typeSymbol.TypeKind == TypeKind.TypeParameter) return;

        // Skip special types (primitives, etc.)
        if (typeSymbol.SpecialType != SpecialType.None) return;

        // Skip error types
        if (typeSymbol.TypeKind == TypeKind.Error) return;

        // Get the containing assembly
        var assembly = typeSymbol.ContainingAssembly;
        if (assembly == null) return;

        var assemblyName = assembly.Name;

        // Skip system assemblies
        if (IsSystemAssembly(assemblyName)) return;

        // Get the type name
        var typeName = typeSymbol.Name;
        var fullName = typeSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat).Split('<')[0];

        // Skip if it's a locally defined type
        if (definedTypes.Contains(typeName) || definedTypes.Contains(fullName)) return;

        // Add to external types if not already present
        if (!externalTypes.ContainsKey(typeName))
        {
            externalTypes[typeName] = (typeSymbol, assemblyName);
        }

        // Recursively process generic type arguments
        if (typeSymbol is INamedTypeSymbol namedType && namedType.TypeArguments.Length > 0)
        {
            foreach (var typeArg in namedType.TypeArguments)
            {
                CollectExternalTypeSymbolRecursive(typeArg, definedTypes, externalTypes);
            }
        }
    }

    /// <summary>
    /// Gets the kind string for a type symbol.
    /// </summary>
    private static string GetTypeKindFromSymbol(ITypeSymbol symbol) => symbol.TypeKind switch
    {
        TypeKind.Class => symbol.IsRecord ? "record" : "class",
        TypeKind.Interface => "interface",
        TypeKind.Struct => symbol.IsRecord ? "record struct" : "struct",
        TypeKind.Enum => "enum",
        TypeKind.Delegate => "delegate",
        _ => "type"
    };

    /// <summary>
    /// Loads runtime metadata references (BCL assemblies). Cached for the process lifetime.
    /// </summary>
    private static IReadOnlyList<MetadataReference> LoadRuntimeReferences()
    {
        List<MetadataReference> references = [];
        var runtimeDir = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(runtimeDir))
        {
            var runtimeAssemblies = new[]
            {
                "System.Runtime.dll",
                "System.Collections.dll",
                "System.Linq.dll",
                "System.Threading.Tasks.dll",
                "System.IO.dll",
                "netstandard.dll",
            };

            foreach (var asm in runtimeAssemblies)
            {
                var path = Path.Combine(runtimeDir, asm);
                if (File.Exists(path))
                {
                    try { references.Add(MetadataReference.CreateFromFile(path)); }
                    catch (Exception ex) { Trace.TraceWarning("Failed to load runtime assembly '{0}': {1}", path, ex.Message); }
                }
            }
        }
        return references;
    }

    /// <summary>
    /// Loads metadata references from cached runtime assemblies and project-specific NuGet packages.
    /// </summary>
    private static IReadOnlyList<MetadataReference> LoadMetadataReferences(string rootPath)
    {
        var references = new List<MetadataReference>(s_runtimeReferences.Value);

        // Look for NuGet packages in common locations
        var nugetPackagesDir = FindNuGetPackagesDirectory(rootPath);
        if (nugetPackagesDir != null)
        {
            // Parse project file to find package references
            var packageRefs = ParseProjectPackageReferences(rootPath);

            foreach (var (packageName, version) in packageRefs)
            {
                var packageDir = Path.Combine(nugetPackagesDir, packageName.ToLowerInvariant(), version);
                if (!Directory.Exists(packageDir))
                {
                    // Try without version for floating versions
                    var packageBaseDir = Path.Combine(nugetPackagesDir, packageName.ToLowerInvariant());
                    if (Directory.Exists(packageBaseDir))
                    {
                        // Get the latest version
                        var versions = Directory.GetDirectories(packageBaseDir)
                            .Select(Path.GetFileName)
                            .OrderByDescending(v => ParseSemVerPrefix(v))
                            .FirstOrDefault();
                        if (versions != null)
                        {
                            packageDir = Path.Combine(packageBaseDir, versions);
                        }
                    }
                }

                if (Directory.Exists(packageDir))
                {
                    // Find the lib folder with the appropriate target framework
                    var libDir = FindBestTargetFramework(packageDir);
                    if (libDir != null)
                    {
                        foreach (var dll in Directory.GetFiles(libDir, "*.dll"))
                        {
                            try { references.Add(MetadataReference.CreateFromFile(dll)); }
                            catch (Exception ex) { Trace.TraceWarning("Failed to load NuGet assembly '{0}': {1}", dll, ex.Message); }
                        }
                    }
                }
            }
        }

        return references;
    }

    /// <summary>
    /// Finds the NuGet packages directory.
    /// </summary>
    private static string? FindNuGetPackagesDirectory(string rootPath)
    {
        // Check for local packages folder
        var localPackages = Path.Combine(rootPath, "packages");
        if (Directory.Exists(localPackages)) return localPackages;

        // Check NUGET_PACKAGES environment variable
        var envPackages = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrEmpty(envPackages) && Directory.Exists(envPackages))
            return envPackages;

        // Default locations
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var defaultDir = Path.Combine(homeDir, ".nuget", "packages");
        if (Directory.Exists(defaultDir)) return defaultDir;

        return null;
    }

    /// <summary>
    /// Parses package references from the project file.
    /// </summary>
    private static IReadOnlyList<(string Name, string Version)> ParseProjectPackageReferences(string rootPath)
    {
        List<(string, string)> results = [];

        var csproj = Directory.EnumerateFiles(rootPath, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault()
                  ?? Directory.EnumerateFiles(rootPath, "*.csproj", SearchOption.AllDirectories).FirstOrDefault();

        if (csproj == null) return results;

        try
        {
            var doc = XDocument.Load(csproj);
            var packageRefs = doc.Descendants()
                .Where(e => e.Name.LocalName == "PackageReference");

            foreach (var pr in packageRefs)
            {
                var name = pr.Attribute("Include")?.Value;
                var version = pr.Attribute("Version")?.Value ?? pr.Element(XName.Get("Version", pr.Name.NamespaceName))?.Value;

                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(version))
                {
                    results.Add((name, version));
                }
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Failed to parse package references from '{0}': {1}", csproj, ex.Message);
        }

        return results;
    }

    /// <summary>
    /// Finds the best target framework folder in a NuGet package.
    /// </summary>
    private static string? FindBestTargetFramework(string packageDir)
    {
        var libDir = Path.Combine(packageDir, "lib");
        if (!Directory.Exists(libDir)) return null;

        // Prefer newer frameworks
        var preferredFrameworks = new[]
        {
            "net10.0", "net9.0", "net8.0", "net7.0", "net6.0", "net5.0",
            "netstandard2.1", "netstandard2.0", "netstandard1.6", "netstandard1.0",
            "netcoreapp3.1", "netcoreapp3.0", "netcoreapp2.1", "netcoreapp2.0",
            "net48", "net472", "net471", "net47", "net462", "net461", "net46", "net45"
        };

        foreach (var fw in preferredFrameworks)
        {
            var fwDir = Path.Combine(libDir, fw);
            if (Directory.Exists(fwDir)) return fwDir;
        }

        // Fall back to any framework folder
        return Directory.GetDirectories(libDir).FirstOrDefault();
    }

    /// <summary>
    /// Parses a NuGet version string into a comparable <see cref="Version"/>, stripping pre-release suffixes.
    /// Handles SemVer correctly (e.g., "10.0.0" sorts after "9.0.0", unlike lexicographic sort).
    /// </summary>
    internal static Version ParseSemVerPrefix(string? version)
    {
        if (string.IsNullOrEmpty(version)) return new Version(0, 0);
        var dashIdx = version.IndexOf('-');
        var versionPart = dashIdx >= 0 ? version[..dashIdx] : version;
        return Version.TryParse(versionPart, out var v) ? v : new Version(0, 0);
    }

    #endregion

    #region Entry Point Detection

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
                var doc = XDocument.Load(csproj);

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
            catch (Exception ex)
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

    #endregion

    /// <summary>
    /// Thread-safe container for merging partial type definitions during parallel extraction.
    /// </summary>
    private sealed class MergedType
    {
        private readonly Lock _lock = new();

        public string Namespace { get; set; } = "";
        public string Name { get; set; } = "";
        public volatile string Kind = "";
        public volatile string? Base;
        public ConcurrentDictionary<string, byte> Interfaces { get; } = new(); // Using dict as concurrent hashset
        public volatile string? Doc;
        public ConcurrentDictionary<string, MemberInfo> Members { get; } = new(); // Key = signature for dedup
        private volatile List<string>? _values;
        public volatile bool IsEntryPoint;
        public ConcurrentBag<string> RawBaseTypes { get; } = [];

        public void AddInterface(string iface) => Interfaces.TryAdd(iface, 0);

        public void SetKindIfEmpty(string kind)
        {
            if (string.IsNullOrEmpty(Kind))
            {
                lock (_lock) { if (string.IsNullOrEmpty(Kind)) Kind = kind; }
            }
        }

        public void SetBaseIfNull(string @base)
        {
            if (Base == null)
            {
                lock (_lock) { Base ??= @base; }
            }
        }

        public void SetDocIfNull(string? doc)
        {
            if (Doc == null && doc != null)
            {
                lock (_lock) { Doc ??= doc; }
            }
        }

        public void SetValuesIfNull(List<string> values)
        {
            if (_values == null)
            {
                lock (_lock) { _values ??= values; }
            }
        }

        /// <summary>
        /// Produces an immutable snapshot. Called after Parallel.ForEachAsync completes,
        /// so all writes are sequenced before this read — no lock needed.
        /// </summary>
        public TypeInfo ToTypeInfo() => new()
        {
            Name = Name,
            Kind = Kind,
            Base = Base,
            Interfaces = !Interfaces.IsEmpty ? Interfaces.Keys.OrderBy(i => i).ToList() : null,
            Doc = Doc,
            Members = !Members.IsEmpty ? Members.Values.ToList() : null,
            Values = _values,
            EntryPoint = IsEntryPoint ? true : null
        };
    }

    private void ExtractFromRoot(SyntaxNode root, ConcurrentDictionary<string, MergedType> typeMap, FrozenSet<string> entryPointNamespaces)
    {
        var fileScopedNs = root.DescendantNodes().OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault();
        if (fileScopedNs != null)
        {
            var nsName = fileScopedNs.Name.ToString();
            var isEntryPointNs = IsEntryPointNamespace(nsName, entryPointNamespaces);
            ExtractTypes(fileScopedNs, nsName, typeMap, isEntryPointNs);
            return;
        }

        foreach (var ns in root.DescendantNodes().OfType<NamespaceDeclarationSyntax>())
        {
            var nsName = ns.Name.ToString();
            var isEntryPointNs = IsEntryPointNamespace(nsName, entryPointNamespaces);
            ExtractTypes(ns, nsName, typeMap, isEntryPointNs);
        }

        foreach (var type in root.ChildNodes().OfType<TypeDeclarationSyntax>().Where(IsPublic))
            MergeType("", type, typeMap, false);
    }

    private void ExtractTypes(SyntaxNode container, string nsName, ConcurrentDictionary<string, MergedType> typeMap, bool isEntryPointNamespace)
    {
        foreach (var type in container.ChildNodes().OfType<BaseTypeDeclarationSyntax>().Where(IsPublic))
            MergeType(nsName, type, typeMap, isEntryPointNamespace);
    }

    private void MergeType(string ns, BaseTypeDeclarationSyntax type, ConcurrentDictionary<string, MergedType> typeMap, bool isEntryPointNamespace)
    {
        var name = GetTypeName(type);
        var key = $"{ns}.{name}";

        var merged = typeMap.GetOrAdd(key, _ => new MergedType { Namespace = ns, Name = name });

        // Update kind (first non-empty wins) - thread-safe
        merged.SetKindIfEmpty(GetTypeKind(type));

        // Mark as entry point if in entry point namespace
        if (isEntryPointNamespace)
        {
            merged.IsEntryPoint = true;
        }

        // Merge base types - thread-safe
        if (type is TypeDeclarationSyntax tds && tds.BaseList != null)
        {
            foreach (var baseType in tds.BaseList.Types)
                merged.RawBaseTypes.Add(baseType.ToString());
        }

        // Take first doc - thread-safe
        merged.SetDocIfNull(GetXmlDoc(type));

        if (type is EnumDeclarationSyntax e)
        {
            merged.SetValuesIfNull(e.Members.Select(m => m.Identifier.Text).ToList());
        }
        else if (type is TypeDeclarationSyntax typeSyntax)
        {
            foreach (var member in typeSyntax.Members.Where(IsPublicMember))
            {
                var info = ExtractMember(member);
                if (info != null)
                    merged.Members.TryAdd(info.Signature, info);
            }
        }
    }

    /// <summary>
    /// Classifies raw base type names collected during parsing into interfaces vs base classes.
    /// Uses locally-parsed type information (from typeMap) for definitive classification.
    /// For the declaring type's own kind:
    ///   - If the type is an interface, all base types are interfaces (C# language rule).
    ///   - If the type is a class/struct, check each base type against known local types.
    ///   - For external types not in our source tree, use the C# naming convention
    ///     (interface names start with 'I' + uppercase, enforced by CA1715).
    /// </summary>
    private static void ClassifyBaseTypes(ConcurrentDictionary<string, MergedType> typeMap)
    {
        // Build lookup: simple type name → kind from all locally-parsed types.
        // This gives us definitive classification for any type defined in the source tree.
        var knownKinds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var mt in typeMap.Values)
        {
            var simpleName = StripGenericParams(mt.Name);
            knownKinds.TryAdd(simpleName, mt.Kind);
        }

        foreach (var mt in typeMap.Values)
        {
            foreach (var rawBase in mt.RawBaseTypes)
            {
                var baseName = StripGenericParams(rawBase);
                // Strip any namespace qualifier for lookup (e.g., "Foo.IBar" → "IBar")
                var simpleName = baseName.LastIndexOf('.') is >= 0 and var dotIdx
                    ? baseName[(dotIdx + 1)..]
                    : baseName;

                if (mt.Kind == "interface")
                {
                    // C# language rule: interfaces can only extend other interfaces.
                    mt.AddInterface(rawBase);
                }
                else if (knownKinds.TryGetValue(simpleName, out var kind))
                {
                    // Known locally-defined type: classify by its actual declared kind.
                    if (kind == "interface")
                        mt.AddInterface(rawBase);
                    else
                        mt.SetBaseIfNull(rawBase);
                }
                else
                {
                    // External type not in our source tree.
                    // Use the C# naming convention (CA1715): interfaces start with 'I' + uppercase.
                    // This is enforced by Roslyn analyzers and universally followed in .NET.
                    if (simpleName.Length >= 2 && simpleName[0] == 'I' && char.IsUpper(simpleName[1]))
                        mt.AddInterface(rawBase);
                    else
                        mt.SetBaseIfNull(rawBase);
                }
            }
        }
    }

    /// <summary>
    /// Strips generic type parameters from a type name.
    /// E.g., "IList&lt;T&gt;" → "IList", "Dictionary&lt;K,V&gt;" → "Dictionary"
    /// </summary>
    private static string StripGenericParams(string name)
    {
        var idx = name.IndexOf('<');
        return idx >= 0 ? name[..idx] : name;
    }

    private static string GetTypeKind(BaseTypeDeclarationSyntax type) => type switch
    {
        RecordDeclarationSyntax r when r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) => "record struct",
        RecordDeclarationSyntax => "record",
        ClassDeclarationSyntax => "class",
        InterfaceDeclarationSyntax => "interface",
        StructDeclarationSyntax => "struct",
        EnumDeclarationSyntax => "enum",
        _ => "type"
    };

    private static string GetTypeName(BaseTypeDeclarationSyntax type)
    {
        var name = type.Identifier.Text;
        if (type is TypeDeclarationSyntax tds && tds.TypeParameterList != null)
            name += $"<{string.Join(",", tds.TypeParameterList.Parameters.Select(p => p.Identifier.Text))}>";
        return name;
    }

    private MemberInfo? ExtractMember(MemberDeclarationSyntax member) => member switch
    {
        ConstructorDeclarationSyntax ctor => new MemberInfo
        {
            Name = ctor.Identifier.Text,
            Kind = "ctor",
            Signature = $"({FormatParams(ctor.ParameterList)})",
            Doc = GetXmlDoc(ctor)
        },
        MethodDeclarationSyntax m => new MemberInfo
        {
            Name = m.Identifier.Text,
            Kind = "method",
            Signature = $"{Simplify(m.ReturnType)} {m.Identifier.Text}{TypeParams(m)}({FormatParams(m.ParameterList)})",
            Doc = GetXmlDoc(m),
            IsStatic = m.Modifiers.Any(SyntaxKind.StaticKeyword) ? true : null,
            IsAsync = IsAsyncMethod(m) ? true : null
        },
        PropertyDeclarationSyntax p => new MemberInfo
        {
            Name = p.Identifier.Text,
            Kind = "property",
            Signature = $"{Simplify(p.Type)} {p.Identifier.Text}{Accessors(p)}",
            Doc = GetXmlDoc(p),
            IsStatic = p.Modifiers.Any(SyntaxKind.StaticKeyword) ? true : null
        },
        IndexerDeclarationSyntax idx => new MemberInfo
        {
            Name = "this[]",
            Kind = "indexer",
            Signature = $"{Simplify(idx.Type)} this[{FormatParams(idx.ParameterList)}]",
            Doc = GetXmlDoc(idx)
        },
        EventDeclarationSyntax evt => new MemberInfo
        {
            Name = evt.Identifier.Text,
            Kind = "event",
            Signature = $"event {Simplify(evt.Type)} {evt.Identifier.Text}",
            Doc = GetXmlDoc(evt)
        },
        FieldDeclarationSyntax f when f.Modifiers.Any(SyntaxKind.ConstKeyword) =>
            f.Declaration.Variables.FirstOrDefault() is { } v ? new MemberInfo
            {
                Name = v.Identifier.Text,
                Kind = "const",
                Signature = $"const {Simplify(f.Declaration.Type)} {v.Identifier.Text}" +
                    (v.Initializer != null && v.Initializer.Value.ToString().Length < 30 ? $" = {v.Initializer.Value}" : ""),
                Doc = GetXmlDoc(f)
            } : null,
        _ => null
    };

    private static string TypeParams(MethodDeclarationSyntax m) =>
        m.TypeParameterList != null
            ? $"<{string.Join(",", m.TypeParameterList.Parameters.Select(p => p.Identifier.Text))}>"
            : "";

    /// <summary>
    /// Determines whether a method is async by checking the async modifier
    /// or by inspecting the return type syntax for Task/ValueTask/IAsyncEnumerable.
    /// Uses Roslyn syntax tree walking instead of string matching.
    /// </summary>
    private static bool IsAsyncMethod(MethodDeclarationSyntax m)
    {
        if (m.Modifiers.Any(SyntaxKind.AsyncKeyword)) return true;
        var name = GetOutermostTypeName(m.ReturnType);
        return name is "Task" or "ValueTask" or "IAsyncEnumerable";
    }

    /// <summary>
    /// Extracts the outermost type identifier from a TypeSyntax node,
    /// stripping namespace qualifiers and ignoring generic type arguments.
    /// E.g., System.Threading.Tasks.Task&lt;int&gt; → "Task"
    /// </summary>
    private static string? GetOutermostTypeName(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        QualifiedNameSyntax q => GetOutermostTypeName(q.Right),
        AliasQualifiedNameSyntax a => GetOutermostTypeName(a.Name),
        _ => null
    };

    private static string Accessors(PropertyDeclarationSyntax p)
    {
        if (p.ExpressionBody != null) return " { get; }";
        if (p.AccessorList == null) return "";

        var hasGet = p.AccessorList.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        var hasSet = p.AccessorList.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
        var hasInit = p.AccessorList.Accessors.Any(a => a.IsKind(SyntaxKind.InitAccessorDeclaration));

        return (hasGet, hasSet, hasInit) switch
        {
            (true, true, _) => " { get; set; }",
            (true, _, true) => " { get; init; }",
            (true, _, _) => " { get; }",
            (_, true, _) => " { set; }",
            _ => ""
        };
    }

    private string FormatParams(ParameterListSyntax? pl) =>
        pl == null ? "" : string.Join(", ", pl.Parameters.Select(FormatParam));

    private string FormatParams(BracketedParameterListSyntax? pl) =>
        pl == null ? "" : string.Join(", ", pl.Parameters.Select(FormatParam));

    private string FormatParam(ParameterSyntax p)
    {
        var mods = string.Join(" ", p.Modifiers.Select(m => m.Text));
        var type = Simplify(p.Type);
        var name = p.Identifier.Text;
        var def = p.Default is not null ? $" = {(p.Default.Value.ToString().Length < 20 ? p.Default.Value.ToString() : "...")}" : "";
        return string.IsNullOrEmpty(mods) ? $"{type} {name}{def}" : $"{mods} {type} {name}{def}";
    }

    /// <summary>
    /// Simplifies a type syntax by stripping well-known System namespace qualifiers.
    /// Uses Roslyn syntax tree walking to only strip leading namespace qualifiers
    /// from QualifiedNameSyntax nodes, never touching type names that happen to
    /// contain "System" as a substring.
    /// </summary>
    private static string Simplify(TypeSyntax? type)
    {
        if (type is null) return "";
        return SimplifyType(type);
    }

    /// <summary>
    /// Recursively walks a TypeSyntax tree, stripping System.* namespace qualifiers
    /// from QualifiedNameSyntax nodes while preserving all other structure.
    /// </summary>
    private static string SimplifyType(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax q when IsSystemNamespace(q.Left.ToString())
            => SimplifyType(q.Right),
        GenericNameSyntax g
            => $"{g.Identifier.Text}<{string.Join(", ", g.TypeArgumentList.Arguments.Select(SimplifyType))}>",
        ArrayTypeSyntax a
            => SimplifyType(a.ElementType) + string.Join("", a.RankSpecifiers),
        NullableTypeSyntax n
            => SimplifyType(n.ElementType) + "?",
        _ => type.ToString()
    };

    /// <summary>
    /// Returns true if the given string represents a System namespace that should be stripped.
    /// Only called with the Left side of a QualifiedNameSyntax, so it always represents
    /// a complete namespace qualifier — never a substring of a type name.
    /// </summary>
    private static bool IsSystemNamespace(string ns) =>
        ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal);

    private static string? GetXmlDoc(SyntaxNode node)
    {
        var trivia = node.GetLeadingTrivia()
            .FirstOrDefault(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                                 t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));

        if (trivia == default) return null;

        var summary = trivia.GetStructure()?.DescendantNodes()
            .OfType<XmlElementSyntax>()
            .FirstOrDefault(e => e.StartTag.Name.ToString() == "summary");

        if (summary == null) return null;

        var text = string.Join(" ", summary.Content
            .OfType<XmlTextSyntax>()
            .SelectMany(t => t.TextTokens)
            .Select(t => t.Text.Trim())).Trim();

        return string.IsNullOrWhiteSpace(text) ? null : text.Length > 150 ? text[..147] + "..." : text;
    }

    private static bool IsPublic(BaseTypeDeclarationSyntax t) => t.Modifiers.Any(SyntaxKind.PublicKeyword);
    /// <summary>
    /// Interface members are implicitly public in C# (no explicit modifier needed).
    /// For classes/structs, the public keyword is required.
    /// </summary>
    private static bool IsPublicMember(MemberDeclarationSyntax m) =>
        m.Modifiers.Any(SyntaxKind.PublicKeyword) || m.Parent is InterfaceDeclarationSyntax;

    private static string DetectPackageName(string rootPath)
    {
        var csproj = Directory.EnumerateFiles(rootPath, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault()
                  ?? Directory.EnumerateFiles(rootPath, "*.csproj", SearchOption.AllDirectories).FirstOrDefault();
        return csproj != null ? Path.GetFileNameWithoutExtension(csproj) : Path.GetFileName(rootPath);
    }

    /// <summary>
    /// Returns true if the relative path contains a directory segment matching <paramref name="segment"/>.
    /// Zero-allocation: avoids per-call string interpolation by checking separator boundaries directly.
    /// </summary>
    internal static bool ContainsSegment(string relativePath, string segment)
    {
        var path = relativePath.AsSpan();
        var seg = segment.AsSpan();
        int searchFrom = 0;
        while (searchFrom <= path.Length - seg.Length)
        {
            var pos = path[searchFrom..].IndexOf(seg, StringComparison.Ordinal);
            if (pos < 0) return false;
            pos += searchFrom;

            var beforeOk = pos == 0 || path[pos - 1] is '/' or '\\';
            var afterPos = pos + seg.Length;
            var afterOk = afterPos < path.Length && path[afterPos] is '/' or '\\';

            if (beforeOk && afterOk) return true;
            searchFrom = pos + 1;
        }

        return false;
    }
}
