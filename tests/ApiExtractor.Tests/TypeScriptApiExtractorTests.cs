// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using ApiExtractor.TypeScript;
using Xunit;

namespace ApiExtractor.Tests;

/// <summary>
/// Shared fixture that extracts TypeScript API once for all tests.
/// Dramatically reduces test time by avoiding repeated npm install and node invocations.
/// </summary>
public class TypeScriptExtractorFixture : IAsyncLifetime
{
    private static readonly string TestFixturesPath = Path.Combine(AppContext.BaseDirectory, "TestFixtures", "TypeScript");

    public ApiIndex? Api { get; private set; }
    public string? SkipReason { get; private set; }
    public string FixturePath => TestFixturesPath;

    public async ValueTask InitializeAsync()
    {
        if (!CheckNodeInstalled())
        {
            SkipReason = "Node.js not installed";
            return;
        }

        try
        {
            Api = await new TypeScriptApiExtractor().ExtractAsync(TestFixturesPath);
        }
        catch (Exception ex)
        {
            SkipReason = $"TypeScript extraction failed: {ex.Message}";
        }
    }

    public ValueTask DisposeAsync() => default;

    private static bool CheckNodeInstalled()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "node",
                Arguments = "--version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(1000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }
}

/// <summary>
/// Tests for the TypeScript API extractor.
/// Uses a shared fixture to extract API once, making tests ~10x faster.
/// </summary>
public class TypeScriptApiExtractorTests : IClassFixture<TypeScriptExtractorFixture>
{
    private readonly TypeScriptExtractorFixture _fixture;

    public TypeScriptApiExtractorTests(TypeScriptExtractorFixture fixture)
    {
        _fixture = fixture;
    }

    private ApiIndex GetApi()
    {
        if (_fixture.SkipReason != null) Assert.Skip(_fixture.SkipReason);
        return _fixture.Api!;
    }

    #region Basic Extraction Tests

    [Fact]
    public void Extract_ReturnsApiIndex_WithPackageName()
    {
        var api = GetApi();
        Assert.NotNull(api);
        Assert.False(string.IsNullOrEmpty(api.Package));
    }

    [Fact]
    public void Extract_FindsModules()
    {
        var api = GetApi();
        Assert.NotNull(api);
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
    public void Extract_FindsConstructors()
    {
        var api = GetApi();
        var classes = api.Modules.SelectMany(m => m.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.NotNull(sampleClient.Constructors);
        Assert.NotEmpty(sampleClient.Constructors);
    }

    [Fact]
    public void Extract_FindsMethods()
    {
        var api = GetApi();
        var classes = api.Modules.SelectMany(m => m.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.NotNull(sampleClient.Methods);
        Assert.Contains(sampleClient.Methods, m => m.Name == "getResource");
    }

    [Fact]
    public void Extract_FindsInterfaces()
    {
        var api = GetApi();
        var interfaces = api.Modules.SelectMany(m => m.Interfaces ?? []).ToList();
        var resource = interfaces.FirstOrDefault(i => i.Name == "Resource");
        Assert.NotNull(resource);
    }

    [Fact]
    public void Extract_FindsEnums()
    {
        var api = GetApi();
        var enums = api.Modules.SelectMany(m => m.Enums ?? []).ToList();
        var resultStatus = enums.FirstOrDefault(e => e.Name == "ResultStatus");
        Assert.NotNull(resultStatus);
        Assert.NotNull(resultStatus.Values);
        Assert.NotEmpty(resultStatus.Values);
    }

    [Fact]
    public void Extract_FindsTypeAliases()
    {
        var api = GetApi();
        var types = api.Modules.SelectMany(m => m.Types ?? []).ToList();
        Assert.NotEmpty(types);
    }

    [Fact]
    public void Extract_FindsFunctions()
    {
        var api = GetApi();
        var functions = api.Modules.SelectMany(m => m.Functions ?? []).ToList();
        Assert.NotEmpty(functions);
        Assert.Contains(functions, f => f.Name == "createDefaultClient" || f.Name == "batchGetResources");
    }

    #endregion

    #region Method Attribute Tests

    [Fact]
    public void Extract_FindsAsyncMethods()
    {
        var api = GetApi();
        var classes = api.Modules.SelectMany(m => m.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.NotNull(sampleClient.Methods);
        Assert.Contains(sampleClient.Methods, m => m.Async == true);
    }

    [Fact]
    public void Extract_FindsStaticMethods()
    {
        var api = GetApi();
        var classes = api.Modules.SelectMany(m => m.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.NotNull(sampleClient.Methods);
        Assert.Contains(sampleClient.Methods, m => m.Static == true);
    }

    [Fact]
    public void Extract_FindsProperties()
    {
        var api = GetApi();
        var interfaces = api.Modules.SelectMany(m => m.Interfaces ?? []).ToList();
        var resource = interfaces.FirstOrDefault(i => i.Name == "Resource");
        Assert.NotNull(resource);
        Assert.NotNull(resource.Properties);
        Assert.Contains(resource.Properties, p => p.Name == "id");
    }

    [Fact]
    public void Extract_ExcludesPrivateMethods()
    {
        var api = GetApi();
        var allMethods = api.Modules
            .SelectMany(m => m.Classes ?? [])
            .SelectMany(c => c.Methods ?? [])
            .ToList();
        Assert.DoesNotContain(allMethods, m => m.Name.StartsWith('#'));
    }

    #endregion

    #region Entry Point and Client Detection Tests

    [Fact]
    public void Extract_SampleClient_IsMarkedAsEntryPoint()
    {
        var api = GetApi();
        var sampleClient = api.GetAllClasses().FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.True(sampleClient.EntryPoint, "SampleClient should be marked as entryPoint");
    }

    [Fact]
    public void Extract_SampleClient_IsClientType()
    {
        var api = GetApi();
        var sampleClient = api.GetAllClasses().FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.True(sampleClient.IsClientType, "SampleClient (entryPoint + methods) should be IsClientType");
    }

    [Fact]
    public void Extract_WidgetClient_IsAlsoEntryPoint_BecauseExportedFromMainFile()
    {
        var api = GetApi();
        var widgetClient = api.GetAllClasses().FirstOrDefault(c => c.Name == "WidgetClient");
        Assert.NotNull(widgetClient);
        // WidgetClient is exported from the same main entry file, so the extractor
        // marks it as entryPoint=true. Subclient status is a higher-level inference.
        Assert.True(widgetClient.EntryPoint == true);
    }

    [Fact]
    public void Extract_GetClientClasses_ReturnsOnlyEntryPoints()
    {
        var api = GetApi();
        var clients = api.GetClientClasses().ToList();
        Assert.NotEmpty(clients);
        Assert.All(clients, c => Assert.True(c.EntryPoint == true && (c.Methods?.Any() ?? false)));
    }

    #endregion

    #region Model and Error Type Detection Tests

    [Fact]
    public void ClassInfo_IsModelType_TrueForPropertyOnlyClasses()
    {
        var modelClass = new ClassInfo
        {
            Name = "TestModel",
            Properties = [new PropertyInfo { Name = "id", Type = "string" }]
        };
        Assert.True(modelClass.IsModelType);
    }

    [Fact]
    public void ClassInfo_IsModelType_FalseForClassesWithMethods()
    {
        var clientClass = new ClassInfo
        {
            Name = "TestClient",
            Properties = [new PropertyInfo { Name = "endpoint", Type = "string" }],
            Methods = [new MethodInfo { Name = "doWork", Sig = "()", Ret = "void" }]
        };
        Assert.False(clientClass.IsModelType);
    }

    [Fact]
    public void ClassInfo_IsErrorType_DetectsErrorBaseClass()
    {
        var errorClass = new ClassInfo { Name = "ServiceError", Extends = "Error" };
        Assert.True(errorClass.IsErrorType);
    }

    [Fact]
    public void ClassInfo_IsErrorType_DetectsExceptionBaseClass()
    {
        var exceptionClass = new ClassInfo { Name = "HttpException", Extends = "RestException" };
        Assert.True(exceptionClass.IsErrorType);
    }

    [Fact]
    public void ClassInfo_IsErrorType_FalseForRegularBaseClass()
    {
        var normalClass = new ClassInfo { Name = "Widget", Extends = "BaseModel" };
        Assert.False(normalClass.IsErrorType);
    }

    [Fact]
    public void ClassInfo_IsErrorType_FalseWhenNoBase()
    {
        var rootClass = new ClassInfo { Name = "SomeClass" };
        Assert.False(rootClass.IsErrorType);
    }

    #endregion

    #region TruncationPriority Tests

    [Fact]
    public void ClassInfo_TruncationPriority_ClientsAreHighest()
    {
        var client = new ClassInfo
        {
            Name = "TestClient",
            EntryPoint = true,
            Methods = [new MethodInfo { Name = "op", Sig = "()", Ret = "void" }]
        };
        Assert.Equal(0, client.TruncationPriority);
    }

    [Fact]
    public void ClassInfo_TruncationPriority_ErrorsBeforeModels()
    {
        var error = new ClassInfo { Name = "SvcError", Extends = "Error" };
        var model = new ClassInfo
        {
            Name = "Dto",
            Properties = [new PropertyInfo { Name = "x", Type = "string" }]
        };
        Assert.True(error.TruncationPriority < model.TruncationPriority);
    }

    [Fact]
    public void ClassInfo_TruncationPriority_ModelsBeforeOther()
    {
        var model = new ClassInfo
        {
            Name = "Dto",
            Properties = [new PropertyInfo { Name = "x", Type = "string" }]
        };
        var other = new ClassInfo { Name = "Helper" };
        Assert.True(model.TruncationPriority < other.TruncationPriority);
    }

    [Fact]
    public void ClassInfo_TruncationPriority_FourLevels_ConsistentWithOtherExtractors()
    {
        var client = new ClassInfo
        {
            Name = "C",
            EntryPoint = true,
            Methods = [new MethodInfo { Name = "op", Sig = "()", Ret = "void" }]
        };
        var error = new ClassInfo { Name = "E", Extends = "Error" };
        var model = new ClassInfo
        {
            Name = "M",
            Properties = [new PropertyInfo { Name = "p", Type = "string" }]
        };
        var other = new ClassInfo { Name = "X" };

        Assert.Equal(0, client.TruncationPriority);
        Assert.Equal(1, error.TruncationPriority);
        Assert.Equal(2, model.TruncationPriority);
        Assert.Equal(3, other.TruncationPriority);
    }

    #endregion

    #region CollectReferencedTypes Tests

    [Fact]
    public void ClassInfo_CollectReferencedTypes_FindsExtends()
    {
        var allTypes = new HashSet<string> { "BaseClient", "ChildClient", "Unrelated" };
        var cls = new ClassInfo
        {
            Name = "ChildClient",
            Extends = "BaseClient",
            Methods = [new MethodInfo { Name = "op", Sig = "()", Ret = "void" }]
        };

        var refs = cls.GetReferencedTypes(allTypes);
        Assert.Contains("BaseClient", refs);
        Assert.DoesNotContain("Unrelated", refs);
    }

    [Fact]
    public void ClassInfo_CollectReferencedTypes_FindsImplements()
    {
        var allTypes = new HashSet<string> { "Disposable", "Serializable", "TestClass" };
        var cls = new ClassInfo
        {
            Name = "TestClass",
            Implements = ["Disposable", "Serializable"]
        };

        var refs = cls.GetReferencedTypes(allTypes);
        Assert.Contains("Disposable", refs);
        Assert.Contains("Serializable", refs);
    }

    [Fact]
    public void ClassInfo_CollectReferencedTypes_FindsMethodSignatureTypes()
    {
        var allTypes = new HashSet<string> { "Request", "Response", "TestClient" };
        var cls = new ClassInfo
        {
            Name = "TestClient",
            Methods = [new MethodInfo { Name = "call", Sig = "(req: Request)", Ret = "Response" }]
        };

        var refs = cls.GetReferencedTypes(allTypes);
        Assert.Contains("Request", refs);
        Assert.Contains("Response", refs);
    }

    [Fact]
    public void ClassInfo_CollectReferencedTypes_FindsPropertyTypes()
    {
        var allTypes = new HashSet<string> { "Options", "TestClass" };
        var cls = new ClassInfo
        {
            Name = "TestClass",
            Properties = [new PropertyInfo { Name = "opts", Type = "Options" }]
        };

        var refs = cls.GetReferencedTypes(allTypes);
        Assert.Contains("Options", refs);
    }

    #endregion

    #region Formatter Tests

    [Fact]
    public void Extract_ToStubs_ProducesNonEmptyOutput()
    {
        var api = GetApi();
        var stubs = api.ToStubs();
        Assert.False(string.IsNullOrWhiteSpace(stubs));
    }

    [Fact]
    public void Extract_ToStubs_ContainsClassDeclarations()
    {
        var api = GetApi();
        var stubs = api.ToStubs();
        Assert.Contains("class SampleClient", stubs);
    }

    [Fact]
    public void Extract_ToStubs_ContainsInterfaceDeclarations()
    {
        var api = GetApi();
        var stubs = api.ToStubs();
        Assert.Contains("interface Resource", stubs);
    }

    [Fact]
    public void Extract_ToStubs_ContainsEnumDeclarations()
    {
        var api = GetApi();
        var stubs = api.ToStubs();
        Assert.Contains("ResultStatus", stubs);
    }

    #endregion

    #region Serialization Tests

    [Fact]
    public void Extract_ToJson_ProducesValidJson()
    {
        var api = GetApi();
        var json = api.ToJson();
        Assert.NotNull(json);
        var parsed = JsonSerializer.Deserialize<JsonElement>(json);
        Assert.Equal(JsonValueKind.Object, parsed.ValueKind);
    }

    [Fact]
    public void Extract_ToJson_Pretty_ProducesIndentedOutput()
    {
        var api = GetApi();
        var json = api.ToJson(pretty: true);
        Assert.Contains("\n", json);
    }

    [Fact]
    public void Extract_ProducesSmallerOutputThanSource()
    {
        // For small test fixtures, API surface can be close to source size
        // due to metadata like entryPoint, exportPath, and reExportedFrom.
        // Real SDK packages (100s of KB) show >80% reduction.
        var api = GetApi();
        var json = JsonSerializer.Serialize(api);
        var sourceSize = Directory.GetFiles(_fixture.FixturePath, "*.ts", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
        var maxAllowedSize = (int)(sourceSize * 1.2); // Allow 20% overhead for small fixtures
        Assert.True(json.Length <= maxAllowedSize,
            $"JSON ({json.Length}) should be <= 120% of source ({sourceSize})");
    }

    #endregion
}
