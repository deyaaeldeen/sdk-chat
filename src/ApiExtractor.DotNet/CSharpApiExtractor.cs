// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
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
        var normalizedRoot = Path.GetFullPath(rootPath);
        var files = Directory.EnumerateFiles(rootPath, "*.cs", SearchOption.AllDirectories)
            .Where(f =>
            {
                // Only filter bin/obj directories that are INSIDE the rootPath
                var relativePath = Path.GetRelativePath(normalizedRoot, f);
                return !relativePath.Contains("/obj/") && !relativePath.Contains("\\obj\\")
                    && !relativePath.Contains("/bin/") && !relativePath.Contains("\\bin\\")
                    && !relativePath.StartsWith("obj/") && !relativePath.StartsWith("obj\\")
                    && !relativePath.StartsWith("bin/") && !relativePath.StartsWith("bin\\");
            })
            .ToList();

        // Key: "namespace.TypeName", Value: merged type
        // Using ConcurrentDictionary for thread-safe parallel processing
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
        var dependencies = ResolveTransitiveDependencies(rootPath, syntaxTrees.ToList(), namespaces, ct);

        return new ApiIndex { Package = packageName, Namespaces = namespaces, Dependencies = dependencies };
    }
    
    #region Transitive Dependency Resolution (Semantic Analysis)
    
    /// <summary>
    /// .NET system assemblies that should be excluded from dependencies.
    /// These are part of the runtime/BCL and not external packages.
    /// </summary>
    private static readonly HashSet<string> SystemAssemblyPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "mscorlib", "netstandard", "Microsoft.CSharp",
        "Microsoft.VisualBasic", "Microsoft.Win32", "WindowsBase",
    };
    
    /// <summary>
    /// Checks if an assembly is a system/runtime assembly (not an external dependency).
    /// </summary>
    private static bool IsSystemAssembly(string? assemblyName)
    {
        if (string.IsNullOrEmpty(assemblyName)) return true;
        return SystemAssemblyPrefixes.Any(prefix => 
            assemblyName.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
            assemblyName.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase));
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
        
        foreach (var tree in syntaxTrees)
        {
            ct.ThrowIfCancellationRequested();
            
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
        // Unwrap nullable
        if (typeSymbol is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            var underlyingType = nullable.TypeArguments.FirstOrDefault();
            if (underlyingType != null)
                CollectExternalTypeSymbolRecursive(underlyingType, definedTypes, externalTypes);
            return;
        }
        
        // Handle arrays
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
    /// Loads metadata references from NuGet packages and the runtime.
    /// </summary>
    private static IReadOnlyList<MetadataReference> LoadMetadataReferences(string rootPath)
    {
        var references = new List<MetadataReference>();
        
        // Add runtime references (required for compilation)
        // Use AppContext.BaseDirectory for single-file app compatibility
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
                    catch { /* Ignore inaccessible assemblies */ }
                }
            }
        }
        
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
                            .OrderByDescending(v => v)
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
                            catch { /* Ignore inaccessible assemblies */ }
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
        var results = new List<(string, string)>();
        
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
        catch
        {
            // Ignore parse errors
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
            "net8.0", "net7.0", "net6.0", "net5.0",
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
    
    #endregion
    
    #region Entry Point Detection
    
    /// <summary>
    /// Resolves entry point namespaces from project configuration.
    /// Entry points in .NET are determined by:
    /// 1. RootNamespace from .csproj
    /// 2. PackageId from .csproj
    /// 3. Inferred from project directory name
    /// </summary>
    private static HashSet<string> ResolveEntryPointNamespaces(string rootPath)
    {
        var entryPoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        // Find .csproj file
        var csproj = Directory.EnumerateFiles(rootPath, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault()
                  ?? Directory.EnumerateFiles(rootPath, "*.csproj", SearchOption.AllDirectories).FirstOrDefault();
        
        if (csproj != null)
        {
            var content = File.ReadAllText(csproj);
            
            // Parse RootNamespace
            var rootNs = ExtractCsprojProperty(content, "RootNamespace");
            if (!string.IsNullOrEmpty(rootNs))
            {
                entryPoints.Add(rootNs);
            }
            
            // Parse PackageId
            var packageId = ExtractCsprojProperty(content, "PackageId");
            if (!string.IsNullOrEmpty(packageId))
            {
                entryPoints.Add(packageId);
            }
            
            // Parse AssemblyName
            var assemblyName = ExtractCsprojProperty(content, "AssemblyName");
            if (!string.IsNullOrEmpty(assemblyName))
            {
                entryPoints.Add(assemblyName);
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
        
        return entryPoints;
    }
    
    /// <summary>
    /// Extracts a property value from .csproj XML content.
    /// </summary>
    private static string? ExtractCsprojProperty(string content, string propertyName)
    {
        // Simple regex-based extraction (avoiding full XML parsing for speed)
        var pattern = $"<{propertyName}>([^<]+)</{propertyName}>";
        var match = System.Text.RegularExpressions.Regex.Match(content, pattern);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }
    
    /// <summary>
    /// Checks if a namespace is an entry point namespace.
    /// </summary>
    private static bool IsEntryPointNamespace(string ns, HashSet<string> entryPointNamespaces)
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
                if (!suffix.Contains(".internal") && !suffix.Contains(".implementation"))
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
        private readonly object _lock = new();

        public string Namespace { get; set; } = "";
        public string Name { get; set; } = "";
        public volatile string Kind = "";
        public volatile string? Base;
        public ConcurrentDictionary<string, byte> Interfaces { get; } = new(); // Using dict as concurrent hashset
        public volatile string? Doc;
        public ConcurrentDictionary<string, MemberInfo> Members { get; } = new(); // Key = signature for dedup
        public volatile List<string>? Values;
        public volatile bool IsEntryPoint;

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

        public TypeInfo ToTypeInfo() => new()
        {
            Name = Name,
            Kind = Kind,
            Base = Base,
            Interfaces = !Interfaces.IsEmpty ? Interfaces.Keys.OrderBy(i => i).ToList() : null,
            Doc = Doc,
            Members = !Members.IsEmpty ? Members.Values.ToList() : null,
            Values = Values,
            EntryPoint = IsEntryPoint ? true : null
        };
    }

    private void ExtractFromRoot(SyntaxNode root, ConcurrentDictionary<string, MergedType> typeMap, HashSet<string> entryPointNamespaces)
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
            {
                var baseName = baseType.ToString();
                if (IsInterface(baseName))
                    merged.AddInterface(baseName);
                else
                    merged.SetBaseIfNull(baseName);
            }
        }

        // Take first doc - thread-safe
        merged.SetDocIfNull(GetXmlDoc(type));

        // Merge members
        if (type is EnumDeclarationSyntax e)
        {
            merged.Values = e.Members.Select(m => m.Identifier.Text).ToList();
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
            name += "<" + string.Join(",", tds.TypeParameterList.Parameters.Select(p => p.Identifier.Text)) + ">";
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
            ? "<" + string.Join(",", m.TypeParameterList.Parameters.Select(p => p.Identifier.Text)) + ">"
            : "";

    private static bool IsAsyncMethod(MethodDeclarationSyntax m)
    {
        var ret = m.ReturnType.ToString();
        return m.Modifiers.Any(SyntaxKind.AsyncKeyword) ||
               ret.StartsWith("Task") || ret.StartsWith("ValueTask") || ret.StartsWith("IAsyncEnumerable");
    }

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
        var def = p.Default != null ? " = " + (p.Default.Value.ToString().Length < 20 ? p.Default.Value.ToString() : "...") : "";
        return string.IsNullOrEmpty(mods) ? $"{type} {name}{def}" : $"{mods} {type} {name}{def}";
    }

    private static string Simplify(TypeSyntax? type) =>
        type?.ToString()
            .Replace("System.Threading.Tasks.", "")
            .Replace("System.Collections.Generic.", "")
            .Replace("System.Threading.", "")
            .Replace("System.", "") ?? "";

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

    private bool IsPublic(BaseTypeDeclarationSyntax t) => t.Modifiers.Any(SyntaxKind.PublicKeyword);
    private bool IsPublicMember(MemberDeclarationSyntax m) => m.Modifiers.Any(SyntaxKind.PublicKeyword);
    private static bool IsInterface(string name) => name.Length >= 2 && name[0] == 'I' && char.IsUpper(name[1]);

    private static string DetectPackageName(string rootPath)
    {
        var csproj = Directory.EnumerateFiles(rootPath, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault()
                  ?? Directory.EnumerateFiles(rootPath, "*.csproj", SearchOption.AllDirectories).FirstOrDefault();
        return csproj != null ? Path.GetFileNameWithoutExtension(csproj) : Path.GetFileName(rootPath);
    }
}
