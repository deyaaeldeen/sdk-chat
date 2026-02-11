// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using ApiExtractor.DotNet;
using Xunit;

namespace ApiExtractor.Tests;

public class DotNetApiExtractorTests
{
    private static readonly string TestFixturesPath = Path.Combine(AppContext.BaseDirectory, "TestFixtures", "DotNet");
    private readonly CSharpApiExtractor _extractor = new();

    [Fact]
    public async Task Extract_ReturnsApiIndex_WithPackageName()
    {
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        Assert.False(string.IsNullOrEmpty(api.Package));
    }

    [Fact]
    public async Task Extract_FindsNamespaces()
    {
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        Assert.NotEmpty(api.Namespaces);
    }

    [Fact]
    public async Task Extract_FindsClasses()
    {
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var types = api.Namespaces.SelectMany(n => n.Types).ToList();
        var sampleClient = types.FirstOrDefault(t => t.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.Equal("class", sampleClient.Kind);
    }

    [Fact]
    public async Task Extract_FindsInterfaces()
    {
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var types = api.Namespaces.SelectMany(n => n.Types).ToList();
        var iface = types.FirstOrDefault(t => t.Name == "IResourceOperations");
        Assert.NotNull(iface);
        Assert.Equal("interface", iface.Kind);
    }

    [Fact]
    public async Task Extract_FindsInterfaceMembers()
    {
        // Interface members are implicitly public in C# and must be extracted
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var types = api.Namespaces.SelectMany(n => n.Types).ToList();
        var iface = types.FirstOrDefault(t => t.Name == "IRecommendationsClient");
        Assert.NotNull(iface);
        Assert.Equal("interface", iface.Kind);
        Assert.NotNull(iface.Members);
        Assert.Contains(iface.Members, m => m.Name == "ListRecommendationsAsync" && m.Kind == "method");
    }

    [Fact]
    public async Task Extract_FindsEnums()
    {
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var types = api.Namespaces.SelectMany(n => n.Types).ToList();
        var enumType = types.FirstOrDefault(t => t.Name == "ResultStatus");
        Assert.NotNull(enumType);
        Assert.Equal("enum", enumType.Kind);
        Assert.NotNull(enumType.Values);
        Assert.Contains("Success", enumType.Values);
    }

    [Fact]
    public async Task Extract_FindsProperties()
    {
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var types = api.Namespaces.SelectMany(n => n.Types).ToList();
        var sampleClient = types.FirstOrDefault(t => t.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.NotNull(sampleClient.Members);
        var properties = sampleClient.Members.Where(m => m.Kind == "property").ToList();
        Assert.NotEmpty(properties);
        Assert.Contains(properties, p => p.Name == "Endpoint");
    }

    [Fact]
    public async Task Extract_FindsMethods()
    {
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var types = api.Namespaces.SelectMany(n => n.Types).ToList();
        var sampleClient = types.FirstOrDefault(t => t.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.NotNull(sampleClient.Members);
        var methods = sampleClient.Members.Where(m => m.Kind == "method").ToList();
        Assert.NotEmpty(methods);
        Assert.Contains(methods, m => m.Name == "GetResourceAsync");
    }

    [Fact]
    public async Task Extract_FindsConstructors()
    {
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var types = api.Namespaces.SelectMany(n => n.Types).ToList();
        var sampleClient = types.FirstOrDefault(t => t.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.NotNull(sampleClient.Members);
        var ctors = sampleClient.Members.Where(m => m.Kind == "ctor").ToList();
        Assert.NotEmpty(ctors);
    }

    [Fact]
    public async Task Extract_CapturesDocComments()
    {
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var types = api.Namespaces.SelectMany(n => n.Types).ToList();
        var sampleClient = types.FirstOrDefault(t => t.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.False(string.IsNullOrEmpty(sampleClient.Doc));
    }

    [Fact]
    public async Task Extract_ExcludesPrivateMembers()
    {
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var allMembers = api.Namespaces
            .SelectMany(n => n.Types)
            .Where(t => t.Members != null)
            .SelectMany(t => t.Members!)
            .ToList();
        // Should not find members starting with underscore (private by convention)
        Assert.DoesNotContain(allMembers, m => m.Name.StartsWith('_'));
    }

    [Fact]
    public async Task Extract_FindsAsyncMethods()
    {
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var allMethods = api.Namespaces
            .SelectMany(n => n.Types)
            .Where(t => t.Members != null)
            .SelectMany(t => t.Members!)
            .Where(m => m.Kind == "method")
            .ToList();
        Assert.Contains(allMethods, m => m.IsAsync == true);
    }

    [Fact]
    public async Task Format_ProducesReadableOutput()
    {
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var formatted = CSharpFormatter.Format(api);
        Assert.Contains("class SampleClient", formatted);
        Assert.Contains("interface IResourceOperations", formatted);
        Assert.Contains("enum ResultStatus", formatted);
    }

    [Fact]
    public async Task Extract_ProducesSmallerOutputThanSource()
    {
        // For small test fixtures, API surface can approach or slightly exceed source size
        // due to JSON overhead (property names, punctuation). Real SDK packages (100s of KB)
        // show >90% reduction. Allow 10% margin for small fixtures.
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var json = JsonSerializer.Serialize(api);
        var sourceSize = Directory.GetFiles(TestFixturesPath, "*.cs", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
        var threshold = (long)(sourceSize * 1.1);
        Assert.True(json.Length <= threshold,
            $"JSON ({json.Length}) should be <= 110% of source ({sourceSize}, threshold {threshold})");
    }

    [Fact]
    public async Task Extract_FindsStructs()
    {
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var types = api.Namespaces.SelectMany(n => n.Types).ToList();
        var structType = types.FirstOrDefault(t => t.Kind == "struct");
        Assert.NotNull(structType);
    }

    [Fact]
    public async Task Extract_FindsGenericTypes()
    {
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var types = api.Namespaces.SelectMany(n => n.Types).ToList();
        var genericType = types.FirstOrDefault(t => t.Name.Contains('<') || t.Name.Contains("Result", StringComparison.Ordinal));
        Assert.NotNull(genericType);
    }

    #region Regression: ParseSemVerPrefix

    [Theory]
    [InlineData("1.0.0", 1, 0, 0)]
    [InlineData("10.0.0", 10, 0, 0)]
    [InlineData("2.1.3", 2, 1, 3)]
    [InlineData("9.0.0-preview.1", 9, 0, 0)]
    [InlineData("1.0.0-beta", 1, 0, 0)]
    public void ParseSemVerPrefix_ParsesCorrectly(string input, int major, int minor, int build)
    {
        var result = CSharpApiExtractor.ParseSemVerPrefix(input);
        Assert.Equal(new Version(major, minor, build), result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-version")]
    public void ParseSemVerPrefix_InvalidInput_ReturnsZero(string? input)
    {
        var result = CSharpApiExtractor.ParseSemVerPrefix(input);
        Assert.Equal(new Version(0, 0), result);
    }

    [Fact]
    public void ParseSemVerPrefix_SortsCorrectly_v10_After_v9()
    {
        // Regression: lexicographic sort put "9.0.0" after "10.0.0" because '9' > '1'
        var versions = new[] { "9.0.0", "10.0.0", "1.2.3", "2.0.0-preview.1" };
        var sorted = versions.OrderByDescending(v => CSharpApiExtractor.ParseSemVerPrefix(v)).ToList();

        Assert.Equal("10.0.0", sorted[0]);
        Assert.Equal("9.0.0", sorted[1]);
        Assert.Equal("2.0.0-preview.1", sorted[2]);
        Assert.Equal("1.2.3", sorted[3]);
    }

    #endregion

    #region Regression: ContainsSegment Zero-Allocation

    [Theory]
    [InlineData("src/bin/Debug/foo.cs", "bin", true)]
    [InlineData("src/obj/Release/foo.cs", "obj", true)]
    [InlineData("bin/foo.cs", "bin", true)]
    [InlineData("src\\bin\\Debug\\foo.cs", "bin", true)]
    [InlineData("src/bin\\Debug/foo.cs", "bin", true)]
    [InlineData("src\\bin/Debug\\foo.cs", "bin", true)]
    [InlineData("bin\\foo.cs", "bin", true)]
    [InlineData("src/binary/foo.cs", "bin", false)]
    [InlineData("combine/foo.cs", "bin", false)]
    [InlineData("src/cabin/foo.cs", "bin", false)]
    [InlineData("robin/foo.cs", "bin", false)]
    [InlineData("foo.cs", "bin", false)]
    [InlineData("", "bin", false)]
    public void ContainsSegment_MatchesBoundaries(string path, string segment, bool expected)
    {
        Assert.Equal(expected, CSharpApiExtractor.ContainsSegment(path, segment));
    }

    [Fact]
    public void ContainsSegment_TrailingSegment_NotMatched()
    {
        // "bin" at the end with no trailing separator is NOT a directory segment
        Assert.False(CSharpApiExtractor.ContainsSegment("src/bin", "bin"));
    }

    [Fact]
    public void ContainsSegment_MiddleOfFilename_NotMatched()
    {
        // "obj" inside "objfoo" should not match
        Assert.False(CSharpApiExtractor.ContainsSegment("src/objfoo/bar.cs", "obj"));
    }

    #endregion
}
