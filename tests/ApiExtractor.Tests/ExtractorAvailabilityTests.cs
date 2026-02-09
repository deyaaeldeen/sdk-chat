// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using ApiExtractor.Contracts;
using Xunit;

namespace ApiExtractor.Tests;

/// <summary>
/// Tests for ExtractorAvailability and ExtractorAvailabilityResult.
/// These tests modify global static state (cache, environment variables)
/// so they must not run in parallel with other tests.
/// </summary>
[Collection("ExtractorAvailability")]
public class ExtractorAvailabilityTests : IDisposable
{
    public ExtractorAvailabilityTests()
    {
        ExtractorAvailability.ClearCache();
    }

    public void Dispose()
    {
        ExtractorAvailability.ClearCache();
    }

    #region ExtractorMode Enum

    [Fact]
    public void ExtractorMode_HasExpectedValues()
    {
        Assert.Equal(0, (int)ExtractorMode.Unavailable);
        Assert.Equal(1, (int)ExtractorMode.NativeBinary);
        Assert.Equal(2, (int)ExtractorMode.RuntimeInterpreter);
        Assert.Equal(3, (int)ExtractorMode.Docker);
    }

    #endregion

    #region ExtractorAvailabilityResult Factory Methods

    [Fact]
    public void Unavailable_SetsCorrectProperties()
    {
        var result = ExtractorAvailabilityResult.Unavailable("not found");

        Assert.False(result.IsAvailable);
        Assert.Equal(ExtractorMode.Unavailable, result.Mode);
        Assert.Null(result.ExecutablePath);
        Assert.Equal("not found", result.UnavailableReason);
        Assert.Null(result.Warning);
        Assert.Null(result.DockerImageName);
    }

    [Fact]
    public void NativeBinary_SetsCorrectProperties()
    {
        var result = ExtractorAvailabilityResult.NativeBinary("/app/go_extractor");

        Assert.True(result.IsAvailable);
        Assert.Equal(ExtractorMode.NativeBinary, result.Mode);
        Assert.Equal("/app/go_extractor", result.ExecutablePath);
        Assert.Null(result.UnavailableReason);
        Assert.Null(result.Warning);
        Assert.Null(result.DockerImageName);
    }

    [Fact]
    public void NativeBinary_WithWarning_SetsWarning()
    {
        var result = ExtractorAvailabilityResult.NativeBinary("/app/go_extractor", "using fallback");

        Assert.True(result.IsAvailable);
        Assert.Equal(ExtractorMode.NativeBinary, result.Mode);
        Assert.Equal("/app/go_extractor", result.ExecutablePath);
        Assert.Equal("using fallback", result.Warning);
    }

    [Fact]
    public void RuntimeInterpreter_SetsCorrectProperties()
    {
        var result = ExtractorAvailabilityResult.RuntimeInterpreter("/usr/bin/python3");

        Assert.True(result.IsAvailable);
        Assert.Equal(ExtractorMode.RuntimeInterpreter, result.Mode);
        Assert.Equal("/usr/bin/python3", result.ExecutablePath);
        Assert.Null(result.UnavailableReason);
        Assert.Null(result.Warning);
        Assert.Null(result.DockerImageName);
    }

    [Fact]
    public void RuntimeInterpreter_WithWarning_SetsWarning()
    {
        var result = ExtractorAvailabilityResult.RuntimeInterpreter("/usr/bin/python3", "old version");

        Assert.True(result.IsAvailable);
        Assert.Equal(ExtractorMode.RuntimeInterpreter, result.Mode);
        Assert.Equal("old version", result.Warning);
    }

    [Fact]
    public void Docker_SetsCorrectProperties()
    {
        var result = ExtractorAvailabilityResult.Docker("api-extractor-go:latest");

        Assert.True(result.IsAvailable);
        Assert.Equal(ExtractorMode.Docker, result.Mode);
        Assert.Null(result.ExecutablePath);
        Assert.Null(result.UnavailableReason);
        Assert.Null(result.Warning);
        Assert.Equal("api-extractor-go:latest", result.DockerImageName);
    }

    [Fact]
    public void Docker_WithCustomImage_UsesProvidedImage()
    {
        var result = ExtractorAvailabilityResult.Docker("myregistry/python-extractor:v2");

        Assert.Equal("myregistry/python-extractor:v2", result.DockerImageName);
    }

    #endregion

    #region ExtractorAvailabilityResult Record Equality

    [Fact]
    public void Result_RecordEquality_Works()
    {
        var a = ExtractorAvailabilityResult.Docker("api-extractor-go:latest");
        var b = ExtractorAvailabilityResult.Docker("api-extractor-go:latest");

        Assert.Equal(a, b);
    }

    [Fact]
    public void Result_RecordEquality_DifferentMode_NotEqual()
    {
        var a = ExtractorAvailabilityResult.NativeBinary("/app/go_extractor");
        var b = ExtractorAvailabilityResult.RuntimeInterpreter("/app/go_extractor");

        Assert.NotEqual(a, b);
    }

    #endregion

    #region Check Caching Behavior

    [Fact]
    public void Check_CachesResult_OnSecondCall()
    {
        // Use a made-up language/tool that definitely doesn't exist
        var result1 = ExtractorAvailability.Check(
            "nonexistent_test_lang",
            "nonexistent_test_binary",
            "nonexistent_test_runtime",
            []);

        var result2 = ExtractorAvailability.Check(
            "nonexistent_test_lang",
            "nonexistent_test_binary",
            "nonexistent_test_runtime",
            []);

        // Should get same reference (cached)
        Assert.Same(result1, result2);
    }

    [Fact]
    public void Check_ForceRecheck_BypassesCache()
    {
        var result1 = ExtractorAvailability.Check(
            "nonexistent_force_test",
            "nonexistent_binary",
            "nonexistent_runtime",
            []);

        var result2 = ExtractorAvailability.Check(
            "nonexistent_force_test",
            "nonexistent_binary",
            "nonexistent_runtime",
            [],
            forceRecheck: true);

        // Both should have the same mode (both fallthrough to Docker or both unavailable)
        Assert.Equal(result1.Mode, result2.Mode);
    }

    [Fact]
    public void ClearCache_AllowsFreshCheck()
    {
        var result1 = ExtractorAvailability.Check(
            "cache_clear_test",
            "nonexistent_binary",
            "nonexistent_runtime",
            []);

        ExtractorAvailability.ClearCache();

        var result2 = ExtractorAvailability.Check(
            "cache_clear_test",
            "nonexistent_binary",
            "nonexistent_runtime",
            []);

        // Both results should have the same mode, but be distinct objects after cache clear
        Assert.Equal(result1.Mode, result2.Mode);
        Assert.NotSame(result1, result2);
    }

    #endregion

    #region Check Fallback Behavior

    [Fact]
    public void Check_WithNonexistentNativeAndRuntime_FallsToDockerOrUnavailable()
    {
        var result = ExtractorAvailability.Check(
            "test_lang",
            "definitely_not_a_real_binary_xyz",
            "definitely_not_a_real_runtime_xyz",
            ["/nonexistent/path"]);

        // With Docker available, falls through to Docker mode; otherwise unavailable
        if (result.IsAvailable)
        {
            Assert.Equal(ExtractorMode.Docker, result.Mode);
            Assert.NotNull(result.DockerImageName);
            Assert.Null(result.UnavailableReason);
        }
        else
        {
            Assert.Equal(ExtractorMode.Unavailable, result.Mode);
            Assert.NotNull(result.UnavailableReason);
            Assert.Contains("test_lang", result.UnavailableReason);
        }
    }

    [Fact]
    public void Check_DockerFallback_UsesPerLanguageImage()
    {
        var result = ExtractorAvailability.Check(
            "go",
            "my_custom_extractor_name",
            "nonexistent_go_runtime_xyz",
            []);

        if (result.Mode == ExtractorMode.Docker)
        {
            // Image should be the per-language default
            Assert.Equal("api-extractor-go:latest", result.DockerImageName);
        }
    }

    [Fact]
    public void Check_UnavailableResult_IncludesHelpfulMessage()
    {
        // Temporarily set Docker image to something that doesn't exist
        // to force unavailable state through all 3 tiers
        var envVar = $"{DockerSandbox.ImageEnvVarPrefix}PYTHON";
        var origImage = Environment.GetEnvironmentVariable(envVar);
        Environment.SetEnvironmentVariable(envVar, "nonexistent_image_for_test:v999");
        ExtractorAvailability.ClearCache();

        try
        {
            var result = ExtractorAvailability.Check(
                "python",
                "nonexistent_python_extractor",
                "nonexistent_python_runtime",
                []);

            // Even if Docker CLI exists, the image won't exist, so it should be unavailable
            // unless a real python runtime is found
            if (!result.IsAvailable)
            {
                Assert.Contains("nonexistent_python_runtime", result.UnavailableReason);
                Assert.Contains("python.org", result.UnavailableReason);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, origImage);
            ExtractorAvailability.ClearCache();
        }
    }

    #endregion
}
