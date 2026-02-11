// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using ApiExtractor.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ApiExtractor.DotNet;

/// <summary>
/// Analyzes C# code to extract which API operations are being used.
/// Uses Roslyn for accurate semantic analysis of method invocations.
/// </summary>
public class CSharpUsageAnalyzer : IUsageAnalyzer<ApiIndex>
{
    /// <inheritdoc />
    public string Language => "csharp";

    /// <summary>
    /// C# analyzer uses Roslyn which is embedded, so it's always available.
    /// </summary>
    public bool IsAvailable() => true;

    /// <inheritdoc />
    public async Task<UsageIndex> AnalyzeAsync(string codePath, ApiIndex apiIndex, CancellationToken ct = default)
    {
        var normalizedPath = Path.GetFullPath(codePath);

        if (!Directory.Exists(normalizedPath))
            return new UsageIndex { FileCount = 0 };

        var clientMethods = BuildClientMethodMap(apiIndex);
        if (clientMethods.Count == 0)
            return new UsageIndex { FileCount = 0 };

        var files = Directory.EnumerateFiles(normalizedPath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("/obj/", StringComparison.Ordinal) && !f.Contains("\\obj\\", StringComparison.Ordinal)
                     && !f.Contains("/bin/", StringComparison.Ordinal) && !f.Contains("\\bin\\", StringComparison.Ordinal))
            .ToList();

        List<SyntaxTree> syntaxTrees = [];
        var filePathMap = new Dictionary<SyntaxTree, string>();

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var code = await File.ReadAllTextAsync(file, ct);
            var tree = CSharpSyntaxTree.ParseText(code, path: file, cancellationToken: ct);
            syntaxTrees.Add(tree);
            filePathMap[tree] = Path.GetRelativePath(normalizedPath, file);
        }

        // Create compilation for semantic analysis with basic references
        // This enables proper type resolution for method receivers
        var references = GetBasicMetadataReferences();
        var compilation = CSharpCompilation.Create(
            "UsageAnalysis",
            syntaxTrees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(NullableContextOptions.Enable));

        // Build property type map from API index for subclient resolution
        var propertyTypeMap = BuildPropertyTypeMap(apiIndex, clientMethods);

        // Build method return type map from API index for precise factory/getter resolution
        var methodReturnTypeMap = BuildMethodReturnTypeMap(apiIndex, clientMethods);

        // Collect all type names from API for variable tracking (including types without methods)
        var allTypeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ns in apiIndex.Namespaces ?? [])
            foreach (var type in ns.Types ?? [])
                allTypeNames.TryAdd(type.Name, type.Name);

        List<OperationUsage> coveredOperations = [];
        HashSet<string> seenOperations = []; // Dedupe: "ClientType.Method"

        foreach (var tree in syntaxTrees)
        {
            ct.ThrowIfCancellationRequested();

            var root = await tree.GetRootAsync(ct);
            var relativePath = filePathMap[tree];
            var semanticModel = compilation.GetSemanticModel(tree);

            // Build variable → client type map for syntactic resolution
            var varTypes = BuildVarTypeMap(root, clientMethods, propertyTypeMap, methodReturnTypeMap, allTypeNames);

            // Use Roslyn to find all method invocations
            var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

            foreach (var invocation in invocations)
            {
                var (clientType, methodName) = ExtractMethodCallWithSemantics(invocation, clientMethods, semanticModel);

                // Fall back to syntactic resolution when Roslyn can't resolve types
                // (e.g., missing assembly references in sample code)
                if (clientType is null)
                {
                    (clientType, methodName) = ExtractMethodCallSyntactic(
                        invocation, clientMethods, varTypes, propertyTypeMap, methodReturnTypeMap);
                }

                if (clientType is not null && methodName is not null)
                {
                    var key = $"{clientType}.{methodName}";
                    if (seenOperations.Add(key))
                    {
                        var lineSpan = invocation.GetLocation().GetLineSpan();
                        coveredOperations.Add(new OperationUsage
                        {
                            ClientType = clientType,
                            Operation = methodName,
                            File = relativePath,
                            Line = lineSpan.StartLinePosition.Line + 1
                        });
                    }
                }
            }
        }

        var uncoveredOperations = BuildUncoveredList(clientMethods, seenOperations, apiIndex);

        return new UsageIndex
        {
            FileCount = files.Count,
            CoveredOperations = coveredOperations,
            UncoveredOperations = uncoveredOperations
        };
    }

    /// <summary>
    /// Gets basic metadata references for compilation.
    /// </summary>
    private static IReadOnlyList<MetadataReference> GetBasicMetadataReferences()
    {
        List<MetadataReference> references = [];
        var runtimeDir = AppContext.BaseDirectory;

        var runtimeAssemblies = new[]
        {
            "System.Runtime.dll",
            "System.Collections.dll",
            "System.Linq.dll",
            "System.Threading.Tasks.dll",
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

        return references;
    }

    /// <inheritdoc />
    public string Format(UsageIndex index) => UsageFormatter.Format(index);

    /// <summary>
    /// Builds a map of client type names to their method names.
    /// </summary>
    private static Dictionary<string, HashSet<string>> BuildClientMethodMap(ApiIndex apiIndex)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var clientType in GetClientAndSubclientTypes(apiIndex))
        {
            var methods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var member in clientType.Members ?? [])
            {
                if (member.Kind == "method")
                {
                    methods.Add(member.Name);
                }
            }
            if (methods.Count > 0)
            {
                map[clientType.Name] = methods;
            }
        }

        return map;
    }

    private static IEnumerable<TypeInfo> GetClientAndSubclientTypes(ApiIndex apiIndex)
    {
        var allTypes = apiIndex.GetAllTypes().ToList();
        var allTypeNames = allTypes
            .Select(t => t.Name.Split('<')[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var interfaceNames = allTypes
            .Where(t => t.Kind.Equals("interface", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Name.Split('<')[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var interfaceImplementers = new Dictionary<string, List<TypeInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in allTypes)
        {
            foreach (var iface in type.Interfaces ?? [])
            {
                var ifaceName = iface.Split('<')[0];
                if (!interfaceImplementers.TryGetValue(ifaceName, out var list))
                {
                    list = [];
                    interfaceImplementers[ifaceName] = list;
                }
                list.Add(type);
            }
        }

        var references = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in allTypes)
        {
            var name = type.Name.Split('<')[0];
            references[name] = type.GetReferencedTypes(allTypeNames);
        }

        var referencedBy = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var refs in references.Values)
        {
            foreach (var target in refs)
            {
                referencedBy[target] = referencedBy.TryGetValue(target, out var count) ? count + 1 : 1;
            }
        }

        var operationTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in allTypes)
        {
            if (type.Members?.Any(m => m.Kind == "method") ?? false)
            {
                operationTypes.Add(type.Name.Split('<')[0]);
            }
        }

        var candidateRoots = allTypes
            .Where(t => t.Kind.Equals("class", StringComparison.OrdinalIgnoreCase)
                     || t.Kind.Equals("struct", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Use EntryPoint field to identify root types (SDK entry points)
        static bool IsRootType(TypeInfo type) => type.EntryPoint == true;

        var rootTypes = candidateRoots
            .Where(type =>
            {
                var name = type.Name.Split('<')[0];
                var hasOperations = type.Members?.Any(m => m.Kind == "method") ?? false;
                var referencesOperations = references.TryGetValue(name, out var refs) && refs.Any(operationTypes.Contains);
                var isReferenced = referencedBy.ContainsKey(name);
                return IsRootType(type) || (!isReferenced && (hasOperations || referencesOperations));
            })
            .ToList();

        if (rootTypes.Count == 0)
        {
            rootTypes = candidateRoots
                .Where(type =>
                {
                    var name = type.Name.Split('<')[0];
                    var hasOperations = type.Members?.Any(m => m.Kind == "method") ?? false;
                    var referencesOperations = references.TryGetValue(name, out var refs) && refs.Any(operationTypes.Contains);
                    return hasOperations || referencesOperations;
                })
                .ToList();
        }

        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();

        foreach (var client in rootTypes)
        {
            var name = client.Name.Split('<')[0];
            if (reachable.Add(name))
            {
                queue.Enqueue(name);
            }
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (references.TryGetValue(current, out var refs))
            {
                foreach (var typeName in refs)
                {
                    if (reachable.Add(typeName))
                    {
                        queue.Enqueue(typeName);
                    }
                }
            }

            if (interfaceNames.Contains(current) && interfaceImplementers.TryGetValue(current, out var implementers))
            {
                foreach (var impl in implementers)
                {
                    var implName = impl.Name.Split('<')[0];
                    if (reachable.Add(implName))
                    {
                        queue.Enqueue(implName);
                    }
                }
            }
        }

        return allTypes
            .Where(t => reachable.Contains(t.Name.Split('<')[0]) && (t.Members?.Any(m => m.Kind == "method") ?? false))
            .GroupBy(t => t.Name.Split('<')[0], StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First());
    }

    /// <summary>
    /// Extracts client type and method name from an invocation using Roslyn semantic analysis.
    /// Uses the semantic model to resolve the actual receiver type for accurate matching.
    /// </summary>
    private static (string? ClientType, string? MethodName) ExtractMethodCallWithSemantics(
        InvocationExpressionSyntax invocation,
        Dictionary<string, HashSet<string>> clientMethods,
        SemanticModel semanticModel)
    {
        // Handle: receiver.Method()
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            return (null, null);

        var methodName = memberAccess.Name.Identifier.Text;

        // First, try semantic resolution for accurate type matching
        try
        {
            var symbolInfo = semanticModel.GetSymbolInfo(memberAccess);
            if (symbolInfo.Symbol is IMethodSymbol methodSymbol)
            {
                var containingType = methodSymbol.ContainingType;
                if (containingType is not null)
                {
                    var typeName = containingType.Name;

                    // Check if this type is in our client methods map
                    if (clientMethods.TryGetValue(typeName, out var methods) && methods.Contains(methodName))
                    {
                        return (typeName, methodName);
                    }

                    // Also check interfaces that the type implements
                    foreach (var iface in containingType.AllInterfaces)
                    {
                        var ifaceName = iface.Name;
                        if (clientMethods.TryGetValue(ifaceName, out var ifaceMethods) && ifaceMethods.Contains(methodName))
                        {
                            return (ifaceName, methodName);
                        }
                    }

                    // Check base types
                    var baseType = containingType.BaseType;
                    while (baseType is not null)
                    {
                        var baseName = baseType.Name;
                        if (clientMethods.TryGetValue(baseName, out var baseMethods) && baseMethods.Contains(methodName))
                        {
                            return (baseName, methodName);
                        }
                        baseType = baseType.BaseType;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Semantic resolution failed (missing references) - fall back to heuristic
            Trace.TraceWarning("Semantic resolution failed for invocation: {0}", ex.Message);
        }

        return (null, null);
    }

    /// <summary>
    /// Builds list of operations that exist in API but have no usage.
    /// Cross-references interface/implementation relationships so that covering
    /// an interface method also covers the implementation (and vice versa).
    /// </summary>
    private static List<UncoveredOperation> BuildUncoveredList(
        Dictionary<string, HashSet<string>> clientMethods,
        HashSet<string> seenOperations,
        ApiIndex apiIndex)
    {
        // Build bidirectional interface ↔ implementation mapping
        var interfaceToImpls = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var implToInterfaces = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var ns in apiIndex.Namespaces ?? [])
        {
            foreach (var type in ns.Types ?? [])
            {
                foreach (var iface in type.Interfaces ?? [])
                {
                    var ifaceName = iface.Split('<')[0];

                    if (!interfaceToImpls.TryGetValue(ifaceName, out var impls))
                    {
                        impls = [];
                        interfaceToImpls[ifaceName] = impls;
                    }
                    impls.Add(type.Name);

                    if (!implToInterfaces.TryGetValue(type.Name, out var ifaces))
                    {
                        ifaces = [];
                        implToInterfaces[type.Name] = ifaces;
                    }
                    ifaces.Add(ifaceName);
                }
            }
        }

        List<UncoveredOperation> uncovered = [];

        foreach (var (clientType, methods) in clientMethods)
        {
            foreach (var method in methods)
            {
                var key = $"{clientType}.{method}";
                if (seenOperations.Contains(key))
                    continue;

                // Check if covered through an interface/implementation relationship
                bool coveredViaRelated = false;

                // If this is an implementation, check if any of its interfaces has the method covered
                if (implToInterfaces.TryGetValue(clientType, out var implementedInterfaces))
                {
                    coveredViaRelated = implementedInterfaces.Any(iface =>
                        seenOperations.Contains($"{iface}.{method}"));
                }

                // If this is an interface, check if any implementation has the method covered
                if (!coveredViaRelated && interfaceToImpls.TryGetValue(clientType, out var implementations))
                {
                    coveredViaRelated = implementations.Any(impl =>
                        seenOperations.Contains($"{impl}.{method}"));
                }

                if (!coveredViaRelated)
                {
                    uncovered.Add(new UncoveredOperation
                    {
                        ClientType = clientType,
                        Operation = method,
                        Signature = $"{method}(...)"
                    });
                }
            }
        }

        return uncovered;
    }

    /// <summary>
    /// Extracts client type and method name using syntactic local type inference.
    /// Used as a fallback when Roslyn semantic model cannot resolve types
    /// (e.g., sample code without assembly references).
    /// </summary>
    private static (string? ClientType, string? MethodName) ExtractMethodCallSyntactic(
        InvocationExpressionSyntax invocation,
        Dictionary<string, HashSet<string>> clientMethods,
        Dictionary<string, string> varTypes,
        Dictionary<string, string> propertyTypeMap,
        Dictionary<string, string> methodReturnTypeMap)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            return (null, null);

        var methodName = memberAccess.Name.Identifier.Text;
        var receiver = memberAccess.Expression;

        // Pattern 1: variable.Method() — e.g., client.GetData()
        if (receiver is IdentifierNameSyntax id)
        {
            var varName = id.Identifier.Text;

            // Check variable type map
            if (varTypes.TryGetValue(varName, out var varType) &&
                clientMethods.TryGetValue(varType, out var methods) &&
                methods.Contains(methodName))
            {
                return (varType, methodName);
            }

            // Check static call: TypeName.Method() — e.g., Helpers.CreateClient()
            if (clientMethods.TryGetValue(varName, out var staticMethods) &&
                staticMethods.Contains(methodName))
            {
                return (GetCanonicalClientName(varName, clientMethods), methodName);
            }
        }
        // Pattern 2: obj.Property.Method() — subclient chain, e.g., client.Widgets.ListWidgetsAsync()
        else if (receiver is MemberAccessExpressionSyntax innerMember &&
                 innerMember.Expression is IdentifierNameSyntax sourceId)
        {
            var sourceVar = sourceId.Identifier.Text;
            var propName = innerMember.Name.Identifier.Text;

            if (varTypes.TryGetValue(sourceVar, out var sourceType))
            {
                // Resolve property type from API data only
                var propKey = $"{sourceType}.{propName}";
                if (propertyTypeMap.TryGetValue(propKey, out var resolvedType) &&
                    clientMethods.TryGetValue(resolvedType, out var methods) &&
                    methods.Contains(methodName))
                {
                    return (resolvedType, methodName);
                }
            }
        }
        // Pattern 3: obj.GetClient().Method() — chained method call returning a client type
        else if (receiver is InvocationExpressionSyntax chainedCall &&
                 chainedCall.Expression is MemberAccessExpressionSyntax chainedAccess &&
                 chainedAccess.Expression is IdentifierNameSyntax chainedReceiverId)
        {
            var chainedReceiverName = chainedReceiverId.Identifier.Text;
            var chainedMethodName = chainedAccess.Name.Identifier.Text;

            if (varTypes.TryGetValue(chainedReceiverName, out var chainedReceiverType))
            {
                var chainedKey = $"{chainedReceiverType}.{chainedMethodName}";
                if (methodReturnTypeMap.TryGetValue(chainedKey, out var chainedRetType) &&
                    clientMethods.TryGetValue(chainedRetType, out var methods) &&
                    methods.Contains(methodName))
                {
                    return (chainedRetType, methodName);
                }
            }
        }

        return (null, null);
    }

    /// <summary>
    /// Builds a map of variable names to their inferred client types from syntax.
    /// Tracks: new Type(), explicit Type x = ..., property access (via API data),
    /// method return types (via API data), and static factory calls.
    /// </summary>
    private static Dictionary<string, string> BuildVarTypeMap(
        SyntaxNode root,
        Dictionary<string, HashSet<string>> clientMethods,
        Dictionary<string, string> propertyTypeMap,
        Dictionary<string, string> methodReturnTypeMap,
        Dictionary<string, string> allTypeNames)
    {
        var varTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var declaration in root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            var varName = declaration.Identifier.Text;
            var initializer = declaration.Initializer?.Value;

            // Pattern: var x = new ChatClient()
            if (initializer is ObjectCreationExpressionSyntax creation)
            {
                var typeName = GetSimpleTypeName(creation.Type);
                if (typeName is not null && allTypeNames.TryGetValue(typeName, out var canonical))
                {
                    varTypes[varName] = canonical;
                    continue;
                }
            }

            // Pattern: ChatClient x = new(...)
            if (initializer is ImplicitObjectCreationExpressionSyntax &&
                declaration.Parent is VariableDeclarationSyntax implDecl)
            {
                var typeName = GetSimpleTypeName(implDecl.Type);
                if (typeName is not null && allTypeNames.TryGetValue(typeName, out var canonical))
                {
                    varTypes[varName] = canonical;
                    continue;
                }
            }

            // Pattern: ChatClient x = ...  (explicit type declaration)
            if (declaration.Parent is VariableDeclarationSyntax explDecl)
            {
                var typeName = GetSimpleTypeName(explDecl.Type);
                if (typeName is not null && typeName != "var" && allTypeNames.TryGetValue(typeName, out var canonical))
                {
                    varTypes[varName] = canonical;
                    continue;
                }
            }

            // Pattern: var blob = storage.Blobs  (property access → subclient type from API data)
            if (initializer is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Expression is IdentifierNameSyntax sourceId)
            {
                var sourceVar = sourceId.Identifier.Text;
                if (varTypes.TryGetValue(sourceVar, out var sourceType))
                {
                    var propName = memberAccess.Name.Identifier.Text;
                    var propKey = $"{sourceType}.{propName}";
                    if (propertyTypeMap.TryGetValue(propKey, out var propType))
                    {
                        varTypes[varName] = propType;
                        continue;
                    }
                }
            }

            // Pattern: var x = obj.GetChatClient() or ChatClient.Create() (method call → return type from API data)
            if (initializer is InvocationExpressionSyntax invocation)
            {
                if (invocation.Expression is MemberAccessExpressionSyntax methodAccess)
                {
                    var methodName = methodAccess.Name.Identifier.Text;

                    // Static factory: ChatClient.Create() — receiver is a known API type
                    if (methodAccess.Expression is IdentifierNameSyntax typeId &&
                        allTypeNames.TryGetValue(typeId.Identifier.Text, out var canonical))
                    {
                        // Check if the receiver type itself is the return type (static factory on self)
                        // Also check method return type map for the actual return type
                        var staticKey = $"{canonical}.{methodName}";
                        if (methodReturnTypeMap.TryGetValue(staticKey, out var staticRetType))
                        {
                            varTypes[varName] = staticRetType;
                        }
                        else
                        {
                            varTypes[varName] = canonical;
                        }
                        continue;
                    }

                    // Instance method: service.GetChatClient() — look up return type from API data
                    if (methodAccess.Expression is IdentifierNameSyntax receiverId &&
                        varTypes.TryGetValue(receiverId.Identifier.Text, out var receiverType))
                    {
                        var methodKey = $"{receiverType}.{methodName}";
                        if (methodReturnTypeMap.TryGetValue(methodKey, out var retType))
                        {
                            varTypes[varName] = retType;
                            continue;
                        }
                    }
                }
            }
        }

        return varTypes;
    }

    /// <summary>
    /// Builds a map of (OwnerType.PropertyName) → ReturnTypeName from API index properties.
    /// </summary>
    private static Dictionary<string, string> BuildPropertyTypeMap(
        ApiIndex apiIndex,
        Dictionary<string, HashSet<string>> clientMethods)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ns in apiIndex.Namespaces ?? [])
        {
            foreach (var type in ns.Types ?? [])
            {
                foreach (var member in type.Members ?? [])
                {
                    if (member.Kind == "property" && member.Signature is not null)
                    {
                        var returnType = ExtractReturnTypeFromPropertySignature(member.Signature);
                        if (returnType is not null && clientMethods.ContainsKey(returnType))
                        {
                            var key = $"{type.Name}.{member.Name}";
                            map[key] = GetCanonicalClientName(returnType, clientMethods);
                        }
                    }
                }
            }
        }

        return map;
    }

    /// <summary>
    /// Extracts the return type from a property signature like "WidgetClient Widgets { get; }".
    /// </summary>
    private static string? ExtractReturnTypeFromPropertySignature(string signature)
    {
        var trimmed = signature.Trim();
        var spaceIdx = trimmed.IndexOf(' ');
        return spaceIdx > 0 ? trimmed[..spaceIdx] : null;
    }

    /// <summary>
    /// Builds a map of (OwnerType.MethodName) → ReturnTypeName from API index method signatures.
    /// Parses method signatures to extract return types, unwrapping async wrappers
    /// (Task&lt;T&gt;, ValueTask&lt;T&gt;). Only includes entries where the return type
    /// is a known API type with methods (i.e., a client/subclient type).
    /// </summary>
    private static Dictionary<string, string> BuildMethodReturnTypeMap(
        ApiIndex apiIndex,
        Dictionary<string, HashSet<string>> clientMethods)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ns in apiIndex.Namespaces ?? [])
        {
            foreach (var type in ns.Types ?? [])
            {
                foreach (var member in type.Members ?? [])
                {
                    if (member.Kind == "method" && member.Signature is not null)
                    {
                        var returnType = ExtractReturnTypeFromMethodSignature(member.Signature);
                        if (returnType is not null)
                        {
                            var unwrapped = UnwrapAsyncReturnType(returnType);
                            if (unwrapped is not null && clientMethods.ContainsKey(unwrapped))
                            {
                                var key = $"{type.Name}.{member.Name}";
                                map[key] = GetCanonicalClientName(unwrapped, clientMethods);
                            }
                        }
                    }
                }
            }
        }

        return map;
    }

    /// <summary>
    /// Extracts the return type from a method signature like "ChatClient GetChatClient(string name)".
    /// Returns the part of the signature before the method name (i.e., the return type).
    /// </summary>
    private static string? ExtractReturnTypeFromMethodSignature(string signature)
    {
        var parenIdx = signature.IndexOf('(');
        if (parenIdx < 0) return null;

        var prefix = signature[..parenIdx].TrimEnd();
        var lastSpaceIdx = prefix.LastIndexOf(' ');
        if (lastSpaceIdx < 0) return null;

        return prefix[..lastSpaceIdx].Trim();
    }

    /// <summary>
    /// Unwraps async wrapper types to get the inner type.
    /// E.g., "Task&lt;BlobClient&gt;" → "BlobClient", "ValueTask&lt;ChatClient&gt;" → "ChatClient".
    /// Returns the type as-is if it's not wrapped.
    /// </summary>
    private static string? UnwrapAsyncReturnType(string returnType)
    {
        ReadOnlySpan<string> wrappers = ["Task", "ValueTask", "IAsyncEnumerable"];
        foreach (var wrapper in wrappers)
        {
            if (returnType.StartsWith(wrapper + "<", StringComparison.Ordinal) &&
                returnType.EndsWith('>'))
            {
                return returnType[(wrapper.Length + 1)..^1];
            }
        }
        return returnType;
    }

    /// <summary>
    /// Gets the canonical (stored) key name from the client methods dictionary.
    /// Needed because the dictionary uses case-insensitive comparison.
    /// </summary>
    private static string GetCanonicalClientName(
        string name,
        Dictionary<string, HashSet<string>> clientMethods)
    {
        foreach (var key in clientMethods.Keys)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return key;
        }
        return name;
    }

    /// <summary>
    /// Extracts the simple type name from a TypeSyntax node.
    /// </summary>
    private static string? GetSimpleTypeName(TypeSyntax type)
    {
        return type switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
            GenericNameSyntax generic => generic.Identifier.Text,
            _ => null
        };
    }
}
