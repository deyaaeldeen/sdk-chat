// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
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

    /// <inheritdoc />
    public async Task<UsageIndex> AnalyzeAsync(string codePath, ApiIndex apiIndex, CancellationToken ct = default)
    {
        var normalizedPath = Path.GetFullPath(codePath);

        if (!Directory.Exists(normalizedPath))
            return new UsageIndex { FileCount = 0 };

        // Get all client + subclient types and their methods from the API
        var clientMethods = BuildClientMethodMap(apiIndex);
        if (clientMethods.Count == 0)
            return new UsageIndex { FileCount = 0 };

        // Find all C# files in the code path (exclude bin/obj)
        var files = Directory.EnumerateFiles(normalizedPath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("/obj/") && !f.Contains("\\obj\\")
                     && !f.Contains("/bin/") && !f.Contains("\\bin\\"))
            .ToList();

        var coveredOperations = new List<OperationUsage>();
        var seenOperations = new HashSet<string>(); // Dedupe: "ClientType.Method"

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var code = await File.ReadAllTextAsync(file, ct);
            var tree = CSharpSyntaxTree.ParseText(code, cancellationToken: ct);
            var root = await tree.GetRootAsync(ct);
            var relativePath = Path.GetRelativePath(normalizedPath, file);

            // Use Roslyn to find all method invocations
            var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

            foreach (var invocation in invocations)
            {
                var (clientType, methodName) = ExtractMethodCall(invocation, clientMethods);
                if (clientType != null && methodName != null)
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

        // Build uncovered operations list
        var uncoveredOperations = BuildUncoveredList(clientMethods, seenOperations);

        return new UsageIndex
        {
            FileCount = files.Count,
            CoveredOperations = coveredOperations,
            UncoveredOperations = uncoveredOperations
        };
    }

    /// <inheritdoc />
    public string Format(UsageIndex index)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"Analyzed {index.FileCount} files.");
        sb.AppendLine();

        if (index.CoveredOperations.Count > 0)
        {
            sb.AppendLine("COVERED OPERATIONS (already have examples):");
            foreach (var op in index.CoveredOperations.OrderBy(o => o.ClientType).ThenBy(o => o.Operation))
            {
                sb.AppendLine($"  - {op.ClientType}.{op.Operation} ({op.File}:{op.Line})");
            }
            sb.AppendLine();
        }

        if (index.UncoveredOperations.Count > 0)
        {
            sb.AppendLine("UNCOVERED OPERATIONS (need examples):");
            foreach (var op in index.UncoveredOperations.OrderBy(o => o.ClientType).ThenBy(o => o.Operation))
            {
                sb.AppendLine($"  - {op.ClientType}.{op.Operation}: {op.Signature}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

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
                    list = new List<TypeInfo>();
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
    /// Extracts client type and method name from an invocation using Roslyn AST.
    /// </summary>
    private static (string? ClientType, string? MethodName) ExtractMethodCall(
        InvocationExpressionSyntax invocation,
        Dictionary<string, HashSet<string>> clientMethods)
    {
        // Handle: receiver.Method() or receiver.MethodAsync()
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            var methodName = memberAccess.Name.Identifier.Text;
            var receiverName = GetReceiverName(memberAccess.Expression);

            if (receiverName != null)
            {
                // Try to match receiver to a known client type
                foreach (var (clientType, methods) in clientMethods)
                {
                    var clientBaseName = clientType
                        .Replace("Client", "")
                        .Replace("Service", "")
                        .Replace("Manager", "");

                    // Match patterns: chatClient, _client, client, service
                    var isClientReceiver =
                        receiverName.Contains(clientBaseName, StringComparison.OrdinalIgnoreCase) ||
                        receiverName.EndsWith("client", StringComparison.OrdinalIgnoreCase) ||
                        receiverName.EndsWith("service", StringComparison.OrdinalIgnoreCase) ||
                        receiverName.StartsWith('_');

                    if (isClientReceiver)
                    {
                        // Check if method matches (with or without Async suffix)
                        if (methods.Contains(methodName))
                            return (clientType, methodName);

                        var withoutAsync = methodName.EndsWith("Async")
                            ? methodName[..^5]
                            : methodName;
                        if (methods.Contains(withoutAsync))
                            return (clientType, methodName);
                    }
                }
            }

            // Fallback: check if any client has this method
            foreach (var (clientType, methods) in clientMethods)
            {
                if (methods.Contains(methodName))
                    return (clientType, methodName);

                var withoutAsync = methodName.EndsWith("Async") ? methodName[..^5] : methodName;
                if (methods.Contains(withoutAsync))
                    return (clientType, methodName);
            }
        }

        return (null, null);
    }

    /// <summary>
    /// Gets the name of the receiver expression.
    /// </summary>
    private static string? GetReceiverName(ExpressionSyntax expression)
    {
        return expression switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
            ThisExpressionSyntax => "this",
            _ => null
        };
    }

    /// <summary>
    /// Builds list of operations that exist in API but have no usage.
    /// </summary>
    private static List<UncoveredOperation> BuildUncoveredList(
        Dictionary<string, HashSet<string>> clientMethods,
        HashSet<string> seenOperations)
    {
        var uncovered = new List<UncoveredOperation>();

        foreach (var (clientType, methods) in clientMethods)
        {
            foreach (var method in methods)
            {
                var key = $"{clientType}.{method}";
                var keyAsync = $"{clientType}.{method}Async";

                if (!seenOperations.Contains(key) && !seenOperations.Contains(keyAsync))
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
}
