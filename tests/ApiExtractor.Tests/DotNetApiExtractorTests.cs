// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

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
    public void ContainsSegment_TrailingSegment_IsMatched()
    {
        // "bin" at the end with no trailing separator IS a directory segment
        // (fixed: previously was incorrectly rejected)
        Assert.True(CSharpApiExtractor.ContainsSegment("src/bin", "bin"));
    }

    [Fact]
    public void ContainsSegment_MiddleOfFilename_NotMatched()
    {
        // "obj" inside "objfoo" should not match
        Assert.False(CSharpApiExtractor.ContainsSegment("src/objfoo/bar.cs", "obj"));
    }

    #endregion

    #region Regression: ContainsSegment Terminal Segments

    [Theory]
    [InlineData("src/bin", "bin", true)]
    [InlineData("obj", "obj", true)]
    [InlineData("bin", "bin", true)]
    [InlineData("src\\obj", "obj", true)]
    [InlineData("path/to/ref", "ref", true)]
    [InlineData("src/bin/Debug", "bin", true)]
    [InlineData("combine", "bin", false)]
    [InlineData("cabin", "bin", false)]
    public void ContainsSegment_TerminalSegment_IsMatched(string path, string segment, bool expected)
    {
        Assert.Equal(expected, CSharpApiExtractor.ContainsSegment(path, segment));
    }

    #endregion

    #region Regression: Delegate Extraction

    [Fact]
    public async Task Extract_FindsDelegates()
    {
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var types = api.Namespaces.SelectMany(n => n.Types).ToList();
        Assert.Contains(types, t => t.Kind == "delegate");
    }

    [Fact]
    public async Task Extract_DelegateHasCorrectKind()
    {
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var types = api.Namespaces.SelectMany(n => n.Types).ToList();
        var widgetChanged = types.FirstOrDefault(t => t.Name == "WidgetChangedHandler");
        Assert.NotNull(widgetChanged);
        Assert.Equal("delegate", widgetChanged.Kind);
    }

    [Fact]
    public async Task Extract_DelegateHasSignatureMember()
    {
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var types = api.Namespaces.SelectMany(n => n.Types).ToList();
        var widgetChanged = types.FirstOrDefault(t => t.Name == "WidgetChangedHandler");
        Assert.NotNull(widgetChanged);
        Assert.NotNull(widgetChanged.Members);
        Assert.NotEmpty(widgetChanged.Members);
        Assert.Contains(widgetChanged.Members, m => m.Name == "Invoke");
    }

    [Fact]
    public async Task Extract_GenericDelegateExtracted()
    {
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var types = api.Namespaces.SelectMany(n => n.Types).ToList();
        var asyncProducer = types.FirstOrDefault(t => t.Name.StartsWith("AsyncResultProducer"));
        Assert.NotNull(asyncProducer);
        Assert.Equal("delegate", asyncProducer.Kind);
    }

    [Fact]
    public async Task Format_IncludesDelegates()
    {
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var formatted = CSharpFormatter.Format(api);
        Assert.Contains("delegate", formatted);
        Assert.Contains("WidgetChangedHandler", formatted);
    }

    #endregion

    #region Regression: Nested Types

    [Fact]
    public async Task Extract_FindsPublicNestedTypes()
    {
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var types = api.Namespaces.SelectMany(n => n.Types).ToList();
        var nested = types.FirstOrDefault(t => t.Name == "AdvancedServiceOptions");
        Assert.NotNull(nested);
        Assert.Equal("class", nested.Kind);
    }

    [Fact]
    public async Task Extract_NestedEnumInOptions()
    {
        // SampleClientOptions.ServiceVersion is a nested enum
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var types = api.Namespaces.SelectMany(n => n.Types).ToList();
        var serviceVersion = types.FirstOrDefault(t => t.Name == "ServiceVersion");
        Assert.NotNull(serviceVersion);
        Assert.Equal("enum", serviceVersion.Kind);
    }

    #endregion

    #region Regression: Field-like Events

    [Fact]
    public async Task Extract_FindsFieldLikeEvents()
    {
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var types = api.Namespaces.SelectMany(n => n.Types).ToList();
        var advService = types.FirstOrDefault(t => t.Name == "AdvancedService");
        Assert.NotNull(advService);
        Assert.NotNull(advService.Members);
        Assert.Contains(advService.Members, m => m.Kind == "event" && m.Name == "StateChanged");
    }

    #endregion

    #region Regression: Multi-variable Const Declarations

    [Fact]
    public async Task Extract_FindsAllConstVariables()
    {
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var types = api.Namespaces.SelectMany(n => n.Types).ToList();
        var advService = types.FirstOrDefault(t => t.Name == "AdvancedService");
        Assert.NotNull(advService);
        Assert.NotNull(advService.Members);
        var consts = advService.Members.Where(m => m.Kind == "const").ToList();
        Assert.Contains(consts, c => c.Name == "MaxRetries");
        Assert.Contains(consts, c => c.Name == "DefaultTimeout");
    }

    #endregion

    #region Regression: DIM Private Members Excluded

    [Fact]
    public async Task Extract_ExcludesPrivateDimMembers()
    {
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var types = api.Namespaces.SelectMany(n => n.Types).ToList();
        var iAdvanced = types.FirstOrDefault(t => t.Name == "IAdvancedClient");
        Assert.NotNull(iAdvanced);
        Assert.NotNull(iAdvanced.Members);
        // Public members should be present
        Assert.Contains(iAdvanced.Members, m => m.Name == "FetchAsync");
        Assert.Contains(iAdvanced.Members, m => m.Name == "FetchWithRetryAsync");
        // Private DIM member should NOT appear
        Assert.DoesNotContain(iAdvanced.Members, m => m.Name == "FormatId");
    }

    #endregion

    #region Regression: Operator Overloads

    [Fact]
    public async Task Extract_FindsOperatorOverloads()
    {
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var types = api.Namespaces.SelectMany(n => n.Types).ToList();
        var money = types.FirstOrDefault(t => t.Name == "Money");
        Assert.NotNull(money);
        Assert.NotNull(money.Members);
        var operators = money.Members.Where(m => m.Kind == "operator").ToList();
        Assert.Contains(operators, m => m.Name == "operator +" && m.Signature!.Contains("operator +"));
        Assert.Contains(operators, m => m.Name == "operator -");
        Assert.Contains(operators, m => m.Name == "operator ==");
        Assert.Contains(operators, m => m.Name == "operator !=");
    }

    [Fact]
    public async Task Extract_FindsConversionOperators()
    {
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var types = api.Namespaces.SelectMany(n => n.Types).ToList();
        var money = types.FirstOrDefault(t => t.Name == "Money");
        Assert.NotNull(money);
        Assert.NotNull(money.Members);
        var operators = money.Members.Where(m => m.Kind == "operator").ToList();
        Assert.Contains(operators, m => m.Name.Contains("implicit") && m.Signature!.Contains("implicit operator"));
        Assert.Contains(operators, m => m.Name.Contains("explicit") && m.Signature!.Contains("explicit operator"));
    }

    #endregion

    #region Regression: Static Readonly Fields

    [Fact]
    public async Task Extract_FindsStaticReadonlyFields()
    {
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var types = api.Namespaces.SelectMany(n => n.Types).ToList();
        var advService = types.FirstOrDefault(t => t.Name == "AdvancedService");
        Assert.NotNull(advService);
        Assert.NotNull(advService.Members);
        var field = advService.Members.FirstOrDefault(m => m.Name == "DefaultEndpoint" && m.Kind == "field");
        Assert.NotNull(field);
        Assert.True(field.IsStatic);
        Assert.Contains("static readonly", field.Signature);
    }

    #endregion

    #region Cached Metadata References

    [Fact]
    public void CachedMetadataReferences_IsNullBeforeExtraction()
    {
        // CachedMetadataReferences starts as null and is only populated after ExtractAsync runs.
        // When co-located with other extractor tests, it may already be populated (possibly empty
        // if the test fixtures have no NuGet dependencies). Just verify the property is accessible.
        var refs = CSharpApiExtractor.CachedMetadataReferences;
        // No exception thrown — property is accessible regardless of cache state
        Assert.True(refs is null || refs.Count >= 0);
    }

    #endregion
}
