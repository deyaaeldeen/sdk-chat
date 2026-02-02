// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Text.Json;
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

        // Parallelize Roslyn parsing - cap at 8 cores to prevent memory pressure
        // Beyond 8 cores, memory bandwidth dominates and additional parallelism hurts
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
                var tree = CSharpSyntaxTree.ParseText(code, cancellationToken: token);
                var root = await tree.GetRootAsync(token).ConfigureAwait(false);
                ExtractFromRoot(root, typeMap);
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

        return new ApiIndex { Package = packageName, Namespaces = namespaces };
    }

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
            Values = Values
        };
    }

    private void ExtractFromRoot(SyntaxNode root, ConcurrentDictionary<string, MergedType> typeMap)
    {
        var fileScopedNs = root.DescendantNodes().OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault();
        if (fileScopedNs != null)
        {
            ExtractTypes(fileScopedNs, fileScopedNs.Name.ToString(), typeMap);
            return;
        }

        foreach (var ns in root.DescendantNodes().OfType<NamespaceDeclarationSyntax>())
            ExtractTypes(ns, ns.Name.ToString(), typeMap);

        foreach (var type in root.ChildNodes().OfType<TypeDeclarationSyntax>().Where(IsPublic))
            MergeType("", type, typeMap);
    }

    private void ExtractTypes(SyntaxNode container, string nsName, ConcurrentDictionary<string, MergedType> typeMap)
    {
        foreach (var type in container.ChildNodes().OfType<BaseTypeDeclarationSyntax>().Where(IsPublic))
            MergeType(nsName, type, typeMap);
    }

    private void MergeType(string ns, BaseTypeDeclarationSyntax type, ConcurrentDictionary<string, MergedType> typeMap)
    {
        var name = GetTypeName(type);
        var key = $"{ns}.{name}";

        var merged = typeMap.GetOrAdd(key, _ => new MergedType { Namespace = ns, Name = name });

        // Update kind (first non-empty wins) - thread-safe
        merged.SetKindIfEmpty(GetTypeKind(type));

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
