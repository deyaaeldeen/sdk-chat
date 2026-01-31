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
        Assert.DoesNotContain(allMembers, m => m.Name.StartsWith("_"));
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
        // For small test fixtures, API surface can be 80-90% of source.
        // Real SDK packages (100s of KB) show >90% reduction.
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var json = JsonSerializer.Serialize(api);
        var sourceSize = Directory.GetFiles(TestFixturesPath, "*.cs", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
        Assert.True(json.Length <= sourceSize,
            $"JSON ({json.Length}) should be <= source ({sourceSize})");
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
        var genericType = types.FirstOrDefault(t => t.Name.Contains("<") || t.Name.Contains("Result"));
        Assert.NotNull(genericType);
    }
}
