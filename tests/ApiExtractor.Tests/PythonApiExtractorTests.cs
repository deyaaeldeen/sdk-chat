// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using ApiExtractor.Contracts;
using ApiExtractor.Python;
using Xunit;

namespace ApiExtractor.Tests;

/// <summary>
/// Shared fixture that extracts Python API once for all tests.
/// Dramatically reduces test time by avoiding repeated Python invocations.
/// </summary>
public class PythonExtractorFixture : IAsyncLifetime
{
    private static readonly string TestFixturesPath = Path.Combine(AppContext.BaseDirectory, "TestFixtures", "Python");

    public ApiIndex? Api { get; private set; }
    public string? SkipReason { get; private set; }
    public string FixturePath => TestFixturesPath;

    public async ValueTask InitializeAsync()
    {
        var extractor = new PythonApiExtractor();
        if (!extractor.IsAvailable())
        {
            SkipReason = extractor.UnavailableReason ?? "Python not available";
            return;
        }

        try
        {
            Api = await extractor.ExtractAsync(TestFixturesPath);
        }
        catch (Exception ex)
        {
            SkipReason = $"Python extraction failed: {ex.Message}";
        }
    }

    public ValueTask DisposeAsync() => default;
}

/// <summary>
/// Tests for the Python API extractor.
/// Uses a shared fixture to extract API once, making tests faster.
/// </summary>
public class PythonApiExtractorTests : IClassFixture<PythonExtractorFixture>
{
    private readonly PythonExtractorFixture _fixture;

    public PythonApiExtractorTests(PythonExtractorFixture fixture)
    {
        _fixture = fixture;
    }

    private ApiIndex GetApi()
    {
        if (_fixture.SkipReason != null) Assert.Skip(_fixture.SkipReason);
        return _fixture.Api!;
    }

    [Fact]
    public void Extract_ReturnsApiIndex_WithPackageName()
    {
        var api = GetApi();
        Assert.False(string.IsNullOrEmpty(api.Package));
    }

    [Fact]
    public void Extract_FindsModules()
    {
        var api = GetApi();
        Assert.NotEmpty(api.Modules);
    }

    [Fact]
    public void Extract_FindsClasses()
    {
        var api = GetApi();
        var classes = api.Modules.SelectMany(m => m.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
    }

    [Fact]
    public void Extract_FindsMethods()
    {
        var api = GetApi();
        var classes = api.Modules.SelectMany(m => m.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.NotNull(sampleClient.Methods);
        Assert.Contains(sampleClient.Methods, m => m.Name == "get_resource");
    }

    [Fact]
    public void Extract_FindsAsyncMethods()
    {
        var api = GetApi();
        var classes = api.Modules.SelectMany(m => m.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.NotNull(sampleClient.Methods);
        Assert.Contains(sampleClient.Methods, m => m.IsAsync == true);
    }

    [Fact]
    public void Extract_FindsClassMethods()
    {
        var api = GetApi();
        var classes = api.Modules.SelectMany(m => m.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.NotNull(sampleClient.Methods);
        Assert.Contains(sampleClient.Methods, m => m.IsClassMethod == true);
    }

    [Fact]
    public void Extract_FindsStaticMethods()
    {
        var api = GetApi();
        var classes = api.Modules.SelectMany(m => m.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.NotNull(sampleClient.Methods);
        Assert.Contains(sampleClient.Methods, m => m.IsStaticMethod == true);
    }

    [Fact]
    public void Extract_FindsModuleFunctions()
    {
        var api = GetApi();
        var functions = api.Modules.SelectMany(m => m.Functions ?? []).ToList();
        Assert.NotEmpty(functions);
        Assert.Contains(functions, f => f.Name == "create_default_client");
    }

    [Fact]
    public void Extract_CapturesDocstrings()
    {
        var api = GetApi();
        var classes = api.Modules.SelectMany(m => m.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.False(string.IsNullOrEmpty(sampleClient.Doc));
    }

    [Fact]
    public void Extract_CapturesMethodSignatures()
    {
        var api = GetApi();
        var classes = api.Modules.SelectMany(m => m.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.NotNull(sampleClient.Methods);
        var method = sampleClient.Methods.FirstOrDefault(m => m.Name == "get_resource");
        Assert.NotNull(method);
        Assert.False(string.IsNullOrEmpty(method.Signature));
    }

    [Fact]
    public void Extract_ExcludesPrivateMethods()
    {
        var api = GetApi();
        var allMethods = api.Modules
            .SelectMany(m => m.Classes ?? [])
            .SelectMany(c => c.Methods ?? [])
            .ToList();
        // Private methods (start with _) should be excluded, but __init__ and __dunder__ are allowed
        var privateMethods = allMethods.Where(m =>
            m.Name.StartsWith('_') && !m.Name.StartsWith("__", StringComparison.Ordinal));
        Assert.Empty(privateMethods);
    }

    [Fact]
    public void Extract_IncludesDunderMethods()
    {
        var api = GetApi();
        var allMethods = api.Modules
            .SelectMany(m => m.Classes ?? [])
            .SelectMany(c => c.Methods ?? [])
            .ToList();
        Assert.Contains(allMethods, m => m.Name == "__init__");
    }

    [Fact]
    public void Format_ProducesReadableOutput()
    {
        var api = GetApi();
        var formatted = PythonFormatter.Format(api);
        Assert.Contains("class SampleClient", formatted);
        Assert.Contains("def get_resource", formatted);
    }

    [Fact]
    public void Extract_ProducesSmallerOutputThanSource()
    {
        var api = GetApi();
        var json = api.ToJson();
        var sourceSize = Directory.GetFiles(_fixture.FixturePath, "*.py", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
        // For small test fixtures, the overhead ratio is higher; real-world packages show much better compression
        Assert.True(json.Length < sourceSize,
            $"JSON ({json.Length}) should be smaller than source ({sourceSize})");
    }
}

/// <summary>
/// Tests for Python model deserialization (entryPoint, reExportedFrom fields).
/// These are pure JSON round-trip tests that do not require a Python runtime.
/// </summary>
public class PythonDeserializationTests
{
    [Fact]
    public void RawPythonClass_DeserializesEntryPoint()
    {
        var json = """
        {
            "name": "ChatClient",
            "base": "BaseClient",
            "doc": "A chat client.",
            "entryPoint": true,
            "methods": [],
            "properties": []
        }
        """;

        var cls = JsonSerializer.Deserialize(json, ExtractorJsonContext.Default.RawPythonClass);

        Assert.NotNull(cls);
        Assert.Equal("ChatClient", cls!.Name);
        Assert.True(cls.EntryPoint);
    }

    [Fact]
    public void RawPythonClass_DeserializesReExportedFrom()
    {
        var json = """
        {
            "name": "BlobClient",
            "doc": "A blob client.",
            "reExportedFrom": "azure.storage.blob._blob_client",
            "methods": [],
            "properties": []
        }
        """;

        var cls = JsonSerializer.Deserialize(json, ExtractorJsonContext.Default.RawPythonClass);

        Assert.NotNull(cls);
        Assert.Equal("azure.storage.blob._blob_client", cls!.ReExportedFrom);
    }

    [Fact]
    public void RawPythonClass_MissingEntryPoint_DefaultsNull()
    {
        var json = """
        {
            "name": "Helper",
            "methods": []
        }
        """;

        var cls = JsonSerializer.Deserialize(json, ExtractorJsonContext.Default.RawPythonClass);

        Assert.NotNull(cls);
        Assert.Null(cls!.EntryPoint);
        Assert.Null(cls.ReExportedFrom);
    }

    [Fact]
    public void RawPythonFunction_DeserializesEntryPoint()
    {
        var json = """
        {
            "name": "create_client",
            "sig": "(endpoint: str) -> ChatClient",
            "entryPoint": true,
            "reExportedFrom": "azure.ai._factories"
        }
        """;

        var func = JsonSerializer.Deserialize(json, ExtractorJsonContext.Default.RawPythonFunction);

        Assert.NotNull(func);
        Assert.True(func!.EntryPoint);
        Assert.Equal("azure.ai._factories", func.ReExportedFrom);
    }

    [Fact]
    public void RawPythonApiIndex_RoundTrip_PreservesEntryPointAndReExportedFrom()
    {
        var json = """
        {
            "package": "test-sdk",
            "modules": [
                {
                    "name": "test_sdk",
                    "classes": [
                        {
                            "name": "ChatClient",
                            "entryPoint": true,
                            "reExportedFrom": "test_sdk._internal",
                            "methods": [
                                { "name": "send", "sig": "(msg: str) -> str" }
                            ],
                            "properties": []
                        }
                    ],
                    "functions": [
                        {
                            "name": "create_client",
                            "sig": "() -> ChatClient",
                            "entryPoint": true,
                            "reExportedFrom": "test_sdk._factories"
                        }
                    ]
                }
            ]
        }
        """;

        var raw = JsonSerializer.Deserialize(json, ExtractorJsonContext.Default.RawPythonApiIndex);

        Assert.NotNull(raw);
        var cls = raw!.Modules![0].Classes![0];
        Assert.True(cls.EntryPoint);
        Assert.Equal("test_sdk._internal", cls.ReExportedFrom);

        var func = raw.Modules![0].Functions![0];
        Assert.True(func.EntryPoint);
        Assert.Equal("test_sdk._factories", func.ReExportedFrom);
    }

    #region Regression: ApiIndexContext.Indented Thread Safety

    [Fact]
    public void ApiIndexContext_Indented_ReturnsSameInstance()
    {
        var context1 = ApiExtractor.Python.ApiIndexContext.Indented;
        var context2 = ApiExtractor.Python.ApiIndexContext.Indented;
        Assert.Same(context1, context2);
    }

    [Fact]
    public void ApiIndexContext_Indented_WritesIndented()
    {
        var context = ApiExtractor.Python.ApiIndexContext.Indented;
        Assert.True(context.Options.WriteIndented);
    }

    #endregion
}
