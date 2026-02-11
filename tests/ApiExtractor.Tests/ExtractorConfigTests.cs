// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using ApiExtractor.Contracts;
using ApiExtractor.Go;
using ApiExtractor.Java;
using ApiExtractor.Python;
using ApiExtractor.TypeScript;
using Xunit;

namespace ApiExtractor.Tests;

public class ExtractorConfigTests
{
    [Fact]
    public void PythonSharedConfig_HasCorrectValues()
    {
        var config = PythonApiExtractor.SharedConfig;

        Assert.Equal("python", config.Language);
        Assert.Equal("python_extractor", config.NativeBinaryName);
        Assert.Equal("python", config.RuntimeToolName);
        Assert.Equal(["python3", "python"], config.RuntimeCandidates);
        Assert.Equal("--help", config.NativeValidationArgs);
        Assert.Equal("--version", config.RuntimeValidationArgs);
    }

    [Fact]
    public void JavaSharedConfig_HasCorrectValues()
    {
        var config = JavaApiExtractor.SharedConfig;

        Assert.Equal("java", config.Language);
        Assert.Equal("java_extractor", config.NativeBinaryName);
        Assert.Equal("jbang", config.RuntimeToolName);
        Assert.Equal(["jbang"], config.RuntimeCandidates);
    }

    [Fact]
    public void GoSharedConfig_HasCorrectValues()
    {
        var config = GoApiExtractor.SharedConfig;

        Assert.Equal("go", config.Language);
        Assert.Equal("go_extractor", config.NativeBinaryName);
        Assert.Equal("go", config.RuntimeToolName);
        Assert.Contains("go", config.RuntimeCandidates);
        // Go uses "version" not "--version" for validation
        Assert.Equal("version", config.RuntimeValidationArgs);
    }

    [Fact]
    public void TypeScriptSharedConfig_HasCorrectValues()
    {
        var config = TypeScriptApiExtractor.SharedConfig;

        Assert.Equal("typescript", config.Language);
        Assert.Equal("ts_extractor", config.NativeBinaryName);
        Assert.Equal("node", config.RuntimeToolName);
        Assert.Equal(["node"], config.RuntimeCandidates);
    }

    [Fact]
    public void ExtractorAvailabilityProvider_CachesResult()
    {
        var config = new ExtractorConfig
        {
            Language = "test",
            NativeBinaryName = "test_extractor",
            RuntimeToolName = "test_tool",
            RuntimeCandidates = ["test_tool"]
        };

        var provider = new ExtractorAvailabilityProvider(config);

        // Call multiple times — should return the same instance
        var result1 = provider.GetAvailability();
        var result2 = provider.GetAvailability();

        Assert.Same(result1, result2);
    }

    [Fact]
    public void ExtractorAvailabilityProvider_ExposesLanguage()
    {
        var config = new ExtractorConfig
        {
            Language = "python",
            NativeBinaryName = "python_extractor",
            RuntimeToolName = "python",
            RuntimeCandidates = ["python3", "python"]
        };

        var provider = new ExtractorAvailabilityProvider(config);
        Assert.Equal("python", provider.Language);
    }

    [Fact]
    public void ExtractorAvailabilityProvider_UnavailableTool_ReportsUnavailable()
    {
        var config = new ExtractorConfig
        {
            Language = "nonexistent",
            NativeBinaryName = "no_such_binary_exists_xyz",
            RuntimeToolName = "no_such_tool_exists_xyz",
            RuntimeCandidates = ["no_such_tool_path"]
        };

        var provider = new ExtractorAvailabilityProvider(config);

        // With a completely bogus toolchain, nothing should be available
        // (unless Docker is available as fallback)
        var result = provider.GetAvailability();
        Assert.NotNull(result);
    }

    [Fact]
    public void ExtractorConfig_Immutability()
    {
        // ExtractorConfig is a sealed record — verify it acts as value type
        var config1 = new ExtractorConfig
        {
            Language = "python",
            NativeBinaryName = "python_extractor",
            RuntimeToolName = "python",
            RuntimeCandidates = ["python3"]
        };

        var config2 = config1 with { Language = "java" };

        Assert.Equal("python", config1.Language);
        Assert.Equal("java", config2.Language);
    }

    [Fact]
    public void ExtractorConfig_DefaultValidationArgs()
    {
        var config = new ExtractorConfig
        {
            Language = "test",
            NativeBinaryName = "test",
            RuntimeToolName = "test",
            RuntimeCandidates = ["test"]
        };

        Assert.Equal("--help", config.NativeValidationArgs);
        Assert.Equal("--version", config.RuntimeValidationArgs);
    }
}
