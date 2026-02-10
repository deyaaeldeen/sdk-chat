// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using ApiExtractor.Contracts;
using Xunit;

namespace ApiExtractor.Tests;

/// <summary>
/// Tests for <see cref="SignatureTokenizer"/> — the shared utility that extracts
/// identifier tokens from type signatures for efficient type-reference lookups.
/// </summary>
public class SignatureTokenizerTests
{
    [Fact]
    public void Tokenize_SimpleTypeName_ReturnsSingleToken()
    {
        var tokens = SignatureTokenizer.Tokenize("MyModel");
        Assert.Equal(["MyModel"], tokens);
    }

    [Fact]
    public void Tokenize_GenericType_SplitsOnAngleBrackets()
    {
        var tokens = SignatureTokenizer.Tokenize("Task<List<MyModel>>");
        Assert.Equal(new HashSet<string> { "Task", "List", "MyModel" }, tokens);
    }

    [Fact]
    public void Tokenize_MethodSignature_ExtractsAllIdentifiers()
    {
        var tokens = SignatureTokenizer.Tokenize("createWidget(options: WidgetOptions): Promise<Widget>");
        Assert.Contains("createWidget", tokens);
        Assert.Contains("options", tokens);
        Assert.Contains("WidgetOptions", tokens);
        Assert.Contains("Promise", tokens);
        Assert.Contains("Widget", tokens);
    }

    [Fact]
    public void Tokenize_GoSignature_HandlesPointerAndSliceSyntax()
    {
        // Go: func NewClient(opts *ClientOptions) (*Client, error)
        var tokens = SignatureTokenizer.Tokenize("func NewClient(opts *ClientOptions) (*Client, error)");
        Assert.Contains("func", tokens);
        Assert.Contains("NewClient", tokens);
        Assert.Contains("opts", tokens);
        Assert.Contains("ClientOptions", tokens);
        Assert.Contains("Client", tokens);
        Assert.Contains("error", tokens);
    }

    [Fact]
    public void Tokenize_PythonSignature_HandlesColonAndArrow()
    {
        // Python: def list_blobs(self, container: str) -> ItemPaged[BlobProperties]
        var tokens = SignatureTokenizer.Tokenize("def list_blobs(self, container: str) -> ItemPaged[BlobProperties]");
        Assert.Contains("def", tokens);
        Assert.Contains("list_blobs", tokens);
        Assert.Contains("self", tokens);
        Assert.Contains("container", tokens);
        Assert.Contains("str", tokens);
        Assert.Contains("ItemPaged", tokens);
        Assert.Contains("BlobProperties", tokens);
    }

    [Fact]
    public void Tokenize_DotNetSignature_HandlesDotSeparatedNames()
    {
        // C#: Azure.Response<BlobDownloadInfo>
        var tokens = SignatureTokenizer.Tokenize("Azure.Response<BlobDownloadInfo>");
        Assert.Contains("Azure", tokens);
        Assert.Contains("Response", tokens);
        Assert.Contains("BlobDownloadInfo", tokens);
    }

    [Fact]
    public void Tokenize_EmptyString_ReturnsEmptySet()
    {
        var tokens = SignatureTokenizer.Tokenize("");
        Assert.Empty(tokens);
    }

    [Fact]
    public void Tokenize_NullString_ReturnsEmptySet()
    {
        var tokens = SignatureTokenizer.Tokenize(null);
        Assert.Empty(tokens);
    }

    [Fact]
    public void Tokenize_OnlyDelimiters_ReturnsEmptySet()
    {
        var tokens = SignatureTokenizer.Tokenize("<>[](),: *&");
        Assert.Empty(tokens);
    }

    [Fact]
    public void Tokenize_UnderscoreInIdentifier_PreservesUnderscore()
    {
        var tokens = SignatureTokenizer.Tokenize("my_type_name");
        Assert.Equal(["my_type_name"], tokens);
    }

    [Fact]
    public void Tokenize_NumbersInIdentifier_PreservesNumbers()
    {
        var tokens = SignatureTokenizer.Tokenize("Model2Config");
        Assert.Equal(["Model2Config"], tokens);
    }

    [Fact]
    public void Tokenize_LeadingDigit_TreatsAsToken()
    {
        // Even though "2Model" isn't a valid identifier in most languages,
        // the tokenizer shouldn't crash and should extract it
        var tokens = SignatureTokenizer.Tokenize("List<2Model>");
        Assert.Contains("List", tokens);
        Assert.Contains("2Model", tokens);
    }

    [Fact]
    public void Tokenize_DuplicateTokens_DeduplicatesAutomatically()
    {
        var tokens = SignatureTokenizer.Tokenize("Map<string, string>");
        Assert.Single(tokens, "string");
        Assert.Contains("Map", tokens);
        Assert.Equal(2, tokens.Count);
    }

    [Fact]
    public void TokenizeInto_AccumulatesAcrossCalls()
    {
        HashSet<string> tokens = [];
        SignatureTokenizer.TokenizeInto("List<Foo>", tokens);
        SignatureTokenizer.TokenizeInto("Map<Bar, Foo>", tokens);

        Assert.Equal(new HashSet<string> { "List", "Foo", "Map", "Bar" }, tokens);
    }

    [Fact]
    public void Tokenize_SubstringTypeName_DoesNotFalseMatch()
    {
        // This tests the key correctness improvement:
        // "ErrorHandler" should NOT produce token "Error"
        var tokens = SignatureTokenizer.Tokenize("ErrorHandler");
        Assert.Contains("ErrorHandler", tokens);
        Assert.DoesNotContain("Error", tokens);
    }

    [Fact]
    public void Tokenize_GenericWithMultipleTypeParams()
    {
        var tokens = SignatureTokenizer.Tokenize("Dictionary<string, List<MyModel>>");
        Assert.Equal(new HashSet<string> { "Dictionary", "string", "List", "MyModel" }, tokens);
    }

    [Fact]
    public void Tokenize_JavaStyleGenerics()
    {
        // Java: PagedIterable<BlobItem>
        var tokens = SignatureTokenizer.Tokenize("PagedIterable<BlobItem>");
        Assert.Equal(new HashSet<string> { "PagedIterable", "BlobItem" }, tokens);
    }

    [Fact]
    public void Tokenize_NullableTypes()
    {
        // C#: string? and TypeScript: string | null
        var tokensCs = SignatureTokenizer.Tokenize("string?");
        Assert.Equal(new HashSet<string> { "string" }, tokensCs);

        var tokensTs = SignatureTokenizer.Tokenize("string | null | undefined");
        Assert.Equal(new HashSet<string> { "string", "null", "undefined" }, tokensTs);
    }

    [Fact]
    public void Tokenize_ArrayTypes()
    {
        // TypeScript: string[], Go: []string
        var tokensTs = SignatureTokenizer.Tokenize("string[]");
        Assert.Equal(new HashSet<string> { "string" }, tokensTs);

        var tokensGo = SignatureTokenizer.Tokenize("[]string");
        Assert.Equal(new HashSet<string> { "string" }, tokensGo);
    }
}
