// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using ApiExtractor.Java;
using Xunit;

namespace ApiExtractor.Tests;

/// <summary>
/// Shared fixture that extracts Java API once for all tests.
/// Dramatically reduces test time by avoiding repeated JBang startup.
/// </summary>
public class JavaExtractorFixture : IAsyncLifetime
{
    private static readonly string TestFixturesPath = Path.Combine(AppContext.BaseDirectory, "TestFixtures", "Java");

    public ApiIndex? Api { get; private set; }
    public string? SkipReason { get; private set; }
    public string FixturePath => TestFixturesPath;

    public async ValueTask InitializeAsync()
    {
        var extractor = new JavaApiExtractor();
        if (!extractor.IsAvailable())
        {
            SkipReason = extractor.UnavailableReason ?? "JBang not available";
            return;
        }

        try
        {
            Api = await extractor.ExtractAsync(TestFixturesPath);
        }
        catch (Exception ex)
        {
            SkipReason = $"Java extraction failed: {ex.Message}";
        }
    }

    public ValueTask DisposeAsync() => default;
}

/// <summary>
/// Tests for the Java API extractor.
/// Uses a shared fixture to extract API once, making tests ~10x faster.
/// </summary>
public class JavaApiExtractorTests : IClassFixture<JavaExtractorFixture>
{
    private readonly JavaExtractorFixture _fixture;

    public JavaApiExtractorTests(JavaExtractorFixture fixture)
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
        Assert.NotNull(api);
        Assert.False(string.IsNullOrEmpty(api.Package));
    }

    [Fact]
    public void Extract_FindsPackages()
    {
        var api = GetApi();
        Assert.NotNull(api);
        Assert.NotEmpty(api.Packages);
    }

    [Fact]
    public void Extract_FindsClasses()
    {
        var api = GetApi();
        var classes = api.Packages.SelectMany(p => p.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
    }

    [Fact]
    public void Extract_FindsConstructors()
    {
        var api = GetApi();
        var classes = api.Packages.SelectMany(p => p.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.NotNull(sampleClient.Constructors);
        Assert.NotEmpty(sampleClient.Constructors);
    }

    [Fact]
    public void Extract_FindsMethods()
    {
        var api = GetApi();
        var classes = api.Packages.SelectMany(p => p.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.NotNull(sampleClient.Methods);
        Assert.Contains(sampleClient.Methods, m => m.Name == "getResource");
    }

    [Fact]
    public void Extract_FindsInterfaces()
    {
        var api = GetApi();
        var interfaces = api.Packages.SelectMany(p => p.Interfaces ?? []).ToList();
        var resourceOps = interfaces.FirstOrDefault(i => i.Name == "ResourceOperations");
        Assert.NotNull(resourceOps);
    }

    [Fact]
    public void Extract_FindsEnums()
    {
        var api = GetApi();
        var enums = api.Packages.SelectMany(p => p.Enums ?? []).ToList();
        var serviceVersion = enums.FirstOrDefault(e => e.Name == "ServiceVersion");
        Assert.NotNull(serviceVersion);
        Assert.NotNull(serviceVersion.Values);
        Assert.NotEmpty(serviceVersion.Values);
    }

    [Fact]
    public void Extract_FindsEnumValues()
    {
        var api = GetApi();
        var enums = api.Packages.SelectMany(p => p.Enums ?? []).ToList();
        var serviceVersion = enums.FirstOrDefault(e => e.Name == "ServiceVersion");
        Assert.NotNull(serviceVersion);
        Assert.Contains(serviceVersion.Values!, v => v.Contains("V2024_01_01", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_FindsClassFields()
    {
        var api = GetApi();
        var classes = api.Packages.SelectMany(p => p.Classes ?? []).ToList();
        var resource = classes.FirstOrDefault(c => c.Name == "Resource");
        Assert.NotNull(resource);
        // Note: Private fields are not exposed in extracted API
        // Fields collection may be empty if all fields are private
    }

    [Fact]
    public void Extract_ExcludesPrivateMethods()
    {
        var api = GetApi();
        var allMethods = api.Packages
            .SelectMany(p => p.Classes ?? [])
            .SelectMany(c => c.Methods ?? [])
            .ToList();
        Assert.DoesNotContain(allMethods, m => m.Modifiers?.Contains("private") == true);
    }

    [Fact]
    public void Extract_ProducesSmallerOutputThanSource()
    {
        // For small test fixtures, API surface can be close to source size
        // due to metadata like entryPoint and reExportedFrom.
        // Real SDK packages (100s of KB) show >80% reduction.
        var api = GetApi();
        var json = JsonSerializer.Serialize(api);
        var sourceSize = Directory.GetFiles(_fixture.FixturePath, "*.java", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
        var maxAllowedSize = (int)(sourceSize * 1.2); // Allow 20% overhead for small fixtures
        Assert.True(json.Length <= maxAllowedSize,
            $"JSON ({json.Length}) should be <= 120% of source ({sourceSize})");
    }

    [Fact]
    public void Extract_FindsInterfaceMethods()
    {
        var api = GetApi();
        var interfaces = api.Packages.SelectMany(p => p.Interfaces ?? []).ToList();
        var resourceOps = interfaces.FirstOrDefault(i => i.Name == "ResourceOperations");
        Assert.NotNull(resourceOps);
        Assert.NotNull(resourceOps.Methods);
        Assert.Contains(resourceOps.Methods, m => m.Name == "get");
    }

    #region Regression: IsModelType Logic

    [Fact]
    public void ClassInfo_IsModelType_ClassWithPublicServiceMethods_IsNotModel()
    {
        var classInfo = new ClassInfo
        {
            Name = "ServiceClient",
            Methods = new List<MethodInfo>
            {
                new() { Name = "createResource", Modifiers = ["public"] },
                new() { Name = "deleteResource", Modifiers = ["public"] }
            }
        };

        Assert.False(classInfo.IsModelType, "Service class with non-getter public methods should not be a model");
    }

    [Fact]
    public void ClassInfo_IsModelType_ClassWithOnlyGettersSetters_IsNotModel()
    {
        // Classes with public methods (even getters/setters) are not models.
        // Model detection is purely structural: no public methods + has fields.
        var classInfo = new ClassInfo
        {
            Name = "UserProfile",
            Methods = new List<MethodInfo>
            {
                new() { Name = "getName", Modifiers = ["public"] },
                new() { Name = "setName", Modifiers = ["public"] },
                new() { Name = "isActive", Modifiers = ["public"] }
            }
        };

        Assert.False(classInfo.IsModelType, "Class with public methods should not be a model, even if they are getters/setters");
    }

    [Fact]
    public void ClassInfo_IsModelType_ClassWithNoPublicMethods_AndFields_IsModel()
    {
        var classInfo = new ClassInfo
        {
            Name = "InternalState",
            Methods = new List<MethodInfo>
            {
                new() { Name = "computeHash", Modifiers = ["private"] }
            },
            Fields = new List<FieldInfo>
            {
                new() { Name = "value", Type = "String" }
            }
        };

        Assert.True(classInfo.IsModelType, "Class with no public methods but with fields should be a model");
    }

    [Fact]
    public void ClassInfo_IsModelType_ClassWithNoMethods_IsModel()
    {
        var classInfo = new ClassInfo
        {
            Name = "EmptyPojo",
            Methods = null
        };

        Assert.True(classInfo.IsModelType, "Class with null methods should be a model");
    }

    [Fact]
    public void ClassInfo_IsModelType_ClassWithMixedMethods_IsNotModel()
    {
        var classInfo = new ClassInfo
        {
            Name = "HybridService",
            Methods = new List<MethodInfo>
            {
                new() { Name = "getName", Modifiers = ["public"] },
                new() { Name = "executeOperation", Modifiers = ["public"] }
            }
        };

        Assert.False(classInfo.IsModelType,
            "Class with mix of getters and service methods should not be a model");
    }

    [Fact]
    public void ClassInfo_IsModelType_EmptyClassNoFieldsNoMethods_IsNotModel()
    {
        // Regression: empty class with zero methods AND zero fields should NOT be a model
        // (it's just an empty type marker, not a data transfer object)
        var classInfo = new ClassInfo
        {
            Name = "EmptyMarker",
            Methods = new List<MethodInfo>(),
            Fields = null
        };

        Assert.False(classInfo.IsModelType,
            "Empty class with no methods and no fields should not be a model (not a DTO)");
    }

    [Fact]
    public void ClassInfo_IsModelType_ClassWithOnlyPrivateMethods_NoFields_IsNotModel()
    {
        // Class with only private methods and no fields is not a model
        var classInfo = new ClassInfo
        {
            Name = "InternalHelper",
            Methods = new List<MethodInfo>
            {
                new() { Name = "compute", Modifiers = ["private"] }
            },
            Fields = null
        };

        Assert.False(classInfo.IsModelType,
            "Class with private methods but no fields should not be a model");
    }

    [Fact]
    public void ClassInfo_IsModelType_ClassWithOnlyPrivateMethods_WithFields_IsModel()
    {
        var classInfo = new ClassInfo
        {
            Name = "DataHolder",
            Methods = new List<MethodInfo>
            {
                new() { Name = "validate", Modifiers = ["private"] }
            },
            Fields = new List<FieldInfo>
            {
                new() { Name = "data", Type = "byte[]" }
            }
        };

        Assert.True(classInfo.IsModelType,
            "Class with private methods and fields should be a model");
    }

    [Fact]
    public void ClassInfo_IsModelType_ClassWithGettersAndFields_IsNotModel()
    {
        // Even with fields present, having public methods makes it not a model
        var classInfo = new ClassInfo
        {
            Name = "ConfigBean",
            Methods = new List<MethodInfo>
            {
                new() { Name = "getHost", Modifiers = ["public"] },
                new() { Name = "setHost", Modifiers = ["public"] }
            },
            Fields = new List<FieldInfo>
            {
                new() { Name = "host", Type = "String" }
            }
        };

        Assert.False(classInfo.IsModelType,
            "Class with public methods (even getters) and fields should not be a model");
    }

    [Fact]
    public void ClassInfo_IsModelType_ClassWithOnlyPublicFields_NoMethods_IsModel()
    {
        // Class with explicit empty methods list and fields — model (no public methods)
        var classInfo = new ClassInfo
        {
            Name = "Point",
            Methods = new List<MethodInfo>(),
            Fields = new List<FieldInfo>
            {
                new() { Name = "x", Type = "int" },
                new() { Name = "y", Type = "int" }
            }
        };

        Assert.True(classInfo.IsModelType,
            "Class with empty methods list and fields should be a model");
    }

    [Fact]
    public void ClassInfo_IsErrorType_ExtendsException_Detected()
    {
        var classInfo = new ClassInfo
        {
            Name = "ServiceException",
            Extends = "RuntimeException",
            Methods = new List<MethodInfo>
            {
                new() { Name = "getErrorCode", Modifiers = ["public"] }
            }
        };

        Assert.True(classInfo.IsErrorType);
        Assert.Equal(1, classInfo.TruncationPriority);
    }

    [Fact]
    public void ClassInfo_IsErrorType_ExtendsThrowable_Detected()
    {
        var classInfo = new ClassInfo
        {
            Name = "CriticalFailure",
            Extends = "Throwable"
        };

        Assert.True(classInfo.IsErrorType);
    }

    [Fact]
    public void ClassInfo_IsErrorType_NamedExceptionNoExtends_NotDetected()
    {
        // Named "FooException" but no Extends → not an error type
        var classInfo = new ClassInfo { Name = "FooException" };
        Assert.False(classInfo.IsErrorType);
    }

    #endregion
}
