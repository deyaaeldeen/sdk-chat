// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.SdkChat.Services;
using Xunit;

namespace Microsoft.SdkChat.Tests.Services;

/// <summary>
/// Tests for API extraction and coverage analysis in PackageInfoService.
/// Entity group: api
/// </summary>
public class ApiServiceTests : PackageInfoTestBase
{
    #region ExtractPublicApiAsync Tests

    [Fact]
    public async Task ExtractPublicApiAsync_DotNetProject_ExtractsApi()
    {
        // Arrange
        var srcDir = Path.Combine(TestRoot, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(TestRoot, "Test.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(srcDir, "MyClient.cs"), @"
namespace TestSdk;

/// <summary>Test client for API operations.</summary>
public class MyClient
{
    /// <summary>Gets a resource by ID.</summary>
    public string GetResource(int id) => ""test"";

    /// <summary>Creates a new resource.</summary>
    public void CreateResource(string name) { }
}
");

        // Act
        var result = await Service.ExtractPublicApiAsync(TestRoot);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("DotNet", result.Language);
        Assert.NotNull(result.ApiSurface);
        Assert.Equal("stubs", result.Format);
        Assert.Contains("MyClient", result.ApiSurface);
        Assert.Contains("GetResource", result.ApiSurface);
    }

    [Fact]
    public async Task ExtractPublicApiAsync_WithJsonFormat_ReturnsJson()
    {
        // Arrange
        var srcDir = Path.Combine(TestRoot, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(TestRoot, "Test.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(srcDir, "MyClient.cs"), @"
public class MyClient { public void DoWork() { } }
");

        // Act
        var result = await Service.ExtractPublicApiAsync(TestRoot, asJson: true);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("json", result.Format);
        Assert.Contains("{", result.ApiSurface); // JSON format
    }

    [Fact]
    public async Task ExtractPublicApiAsync_UnknownLanguage_ReturnsError()
    {
        // Arrange - Empty directory
        File.WriteAllText(Path.Combine(TestRoot, "readme.txt"), "nothing here");

        // Act
        var result = await Service.ExtractPublicApiAsync(TestRoot);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("LANGUAGE_DETECTION_FAILED", result.ErrorCode);
    }

    [Fact]
    public async Task ExtractPublicApiAsync_WithLanguageOverride_UsesOverride()
    {
        // Arrange - Python project but force dotnet extraction
        File.WriteAllText(Path.Combine(TestRoot, "pyproject.toml"), "[project]");
        File.WriteAllText(Path.Combine(TestRoot, "main.py"), "class Foo: pass");

        // Act - Force dotnet, which will fail to find .cs files
        var result = await Service.ExtractPublicApiAsync(TestRoot, language: "dotnet");

        // Assert - Should succeed but find nothing (or minimal)
        Assert.True(result.Success);
        Assert.Equal("DotNet", result.Language);
    }

    #endregion

    #region AnalyzeCoverageAsync Tests

    [Fact]
    public async Task AnalyzeCoverageAsync_NoSamplesFolder_ReturnsError()
    {
        // Arrange - SDK with no samples
        var srcDir = Path.Combine(TestRoot, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(TestRoot, "Test.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(srcDir, "Client.cs"), "public class Client { }");

        // Act
        var result = await Service.AnalyzeCoverageAsync(TestRoot);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("NO_SAMPLES_FOUND", result.ErrorCode);
    }

    [Fact]
    public async Task AnalyzeCoverageAsync_WithSamples_AnalyzesCoverage()
    {
        // Arrange - SDK with source and samples
        var srcDir = Path.Combine(TestRoot, "src");
        var samplesDir = Path.Combine(TestRoot, "samples");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(samplesDir);

        File.WriteAllText(Path.Combine(TestRoot, "Test.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(srcDir, "MyClient.cs"), @"
namespace TestSdk;
public class MyClient
{
    public string GetResource(int id) => ""test"";
    public void CreateResource(string name) { }
    public void DeleteResource(int id) { }
}
");
        // Sample that uses GetResource
        File.WriteAllText(Path.Combine(samplesDir, "GetResourceSample.cs"), @"
using TestSdk;
class Sample
{
    void Run()
    {
        var client = new MyClient();
        client.GetResource(1);
    }
}
");

        // Act
        var result = await Service.AnalyzeCoverageAsync(TestRoot);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(TestRoot + "/src", result.SourceFolder?.Replace('\\', '/'));
        Assert.Equal(samplesDir, result.SamplesFolder);
        Assert.True(result.TotalOperations > 0);
    }

    [Fact]
    public async Task AnalyzeCoverageAsync_WithCustomSamplesPath_UsesIt()
    {
        // Arrange
        var srcDir = Path.Combine(TestRoot, "src");
        var customSamplesDir = Path.Combine(TestRoot, "my-samples");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(customSamplesDir);

        File.WriteAllText(Path.Combine(TestRoot, "Test.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(srcDir, "Client.cs"), "public class Client { public void Work() {} }");
        File.WriteAllText(Path.Combine(customSamplesDir, "Sample.cs"), "class S { void Run() { new Client().Work(); } }");

        // Act
        var result = await Service.AnalyzeCoverageAsync(TestRoot, samplesPath: customSamplesDir);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(customSamplesDir, result.SamplesFolder);
    }

    [Fact]
    public async Task AnalyzeCoverageAsync_UnknownLanguage_ReturnsError()
    {
        // Arrange
        var samplesDir = Path.Combine(TestRoot, "samples");
        Directory.CreateDirectory(samplesDir);
        File.WriteAllText(Path.Combine(samplesDir, "readme.txt"), "samples");

        // Act
        var result = await Service.AnalyzeCoverageAsync(TestRoot);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("LANGUAGE_DETECTION_FAILED", result.ErrorCode);
    }

    #endregion

    #region Interface Tests

    [Fact]
    public void PackageInfoService_ImplementsInterface()
    {
        // Assert
        Assert.IsAssignableFrom<IPackageInfoService>(Service);
    }

    #endregion
}
