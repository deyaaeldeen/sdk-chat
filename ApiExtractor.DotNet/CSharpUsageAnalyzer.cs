// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ApiExtractor.Contracts;

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

        // Get all client types and their methods from the API
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

        foreach (var clientType in apiIndex.GetClientTypes())
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
                        receiverName.StartsWith("_", StringComparison.Ordinal);
                    
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
