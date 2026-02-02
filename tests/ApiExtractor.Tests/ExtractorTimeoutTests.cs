// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using ApiExtractor.Contracts;
using Xunit;

namespace ApiExtractor.Tests;

/// <summary>
/// Tests for ExtractorTimeout configuration.
/// These tests modify environment variables, so they must not run in parallel.
/// </summary>
[Collection("ExtractorTimeout")]
public class ExtractorTimeoutTests : IDisposable
{
    private readonly string? _originalValue;

    public ExtractorTimeoutTests()
    {
        // Save original value
        _originalValue = Environment.GetEnvironmentVariable(ExtractorTimeout.EnvVarName);
        // Reset cache before each test
        ExtractorTimeout.Reset();
    }

    public void Dispose()
    {
        // Restore original value
        if (_originalValue != null)
        {
            Environment.SetEnvironmentVariable(ExtractorTimeout.EnvVarName, _originalValue);
        }
        else
        {
            Environment.SetEnvironmentVariable(ExtractorTimeout.EnvVarName, null);
        }
        // Reset cache after test
        ExtractorTimeout.Reset();
    }

    [Fact]
    public void DefaultTimeout_Is300Seconds()
    {
        // Clear any existing env var
        Environment.SetEnvironmentVariable(ExtractorTimeout.EnvVarName, null);
        ExtractorTimeout.Reset();

        Assert.Equal(TimeSpan.FromSeconds(300), ExtractorTimeout.Value);
    }

    [Fact]
    public void DefaultSeconds_Constant_Is300()
    {
        Assert.Equal(300, ExtractorTimeout.DefaultSeconds);
    }

    [Fact]
    public void EnvVarName_IsCorrect()
    {
        Assert.Equal("SDK_CHAT_EXTRACTOR_TIMEOUT", ExtractorTimeout.EnvVarName);
    }

    [Theory]
    [InlineData("60", 60)]
    [InlineData("120", 120)]
    [InlineData("600", 600)]
    [InlineData("1", 1)]
    [InlineData("3600", 3600)]
    public void Value_ReadsFromEnvironmentVariable(string envValue, int expectedSeconds)
    {
        Environment.SetEnvironmentVariable(ExtractorTimeout.EnvVarName, envValue);
        ExtractorTimeout.Reset();

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), ExtractorTimeout.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("not-a-number")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("-100")]
    public void Value_FallsBackToDefault_WhenEnvVarInvalid(string invalidValue)
    {
        Environment.SetEnvironmentVariable(ExtractorTimeout.EnvVarName, invalidValue);
        ExtractorTimeout.Reset();

        Assert.Equal(TimeSpan.FromSeconds(ExtractorTimeout.DefaultSeconds), ExtractorTimeout.Value);
    }

    [Fact]
    public void Value_IsCached_OnSubsequentCalls()
    {
        Environment.SetEnvironmentVariable(ExtractorTimeout.EnvVarName, "120");
        ExtractorTimeout.Reset();

        var first = ExtractorTimeout.Value;

        // Change env var after first read
        Environment.SetEnvironmentVariable(ExtractorTimeout.EnvVarName, "999");

        var second = ExtractorTimeout.Value;

        // Should still be cached value
        Assert.Equal(first, second);
        Assert.Equal(TimeSpan.FromSeconds(120), second);
    }

    [Fact]
    public void Reset_ClearsCache_AllowsNewValueToBeRead()
    {
        Environment.SetEnvironmentVariable(ExtractorTimeout.EnvVarName, "100");
        ExtractorTimeout.Reset();
        var first = ExtractorTimeout.Value;

        Environment.SetEnvironmentVariable(ExtractorTimeout.EnvVarName, "200");
        ExtractorTimeout.Reset();
        var second = ExtractorTimeout.Value;

        Assert.Equal(TimeSpan.FromSeconds(100), first);
        Assert.Equal(TimeSpan.FromSeconds(200), second);
    }

    [Fact]
    public void Value_HandlesNullEnvironmentVariable()
    {
        Environment.SetEnvironmentVariable(ExtractorTimeout.EnvVarName, null);
        ExtractorTimeout.Reset();

        Assert.Equal(TimeSpan.FromSeconds(300), ExtractorTimeout.Value);
    }

    [Fact]
    public void Value_HandlesLargeValues()
    {
        // 1 hour
        Environment.SetEnvironmentVariable(ExtractorTimeout.EnvVarName, "3600");
        ExtractorTimeout.Reset();

        Assert.Equal(TimeSpan.FromHours(1), ExtractorTimeout.Value);
    }

    [Fact]
    public void Value_HandlesWhitespaceAroundNumber()
    {
        // int.TryParse handles leading/trailing whitespace
        Environment.SetEnvironmentVariable(ExtractorTimeout.EnvVarName, " 180 ");
        ExtractorTimeout.Reset();

        Assert.Equal(TimeSpan.FromSeconds(180), ExtractorTimeout.Value);
    }
}

/// <summary>
/// Collection definition for ExtractorTimeout tests.
/// Ensures tests don't run in parallel due to shared environment variable state.
/// </summary>
[CollectionDefinition("ExtractorTimeout")]
public class ExtractorTimeoutCollection : ICollectionFixture<ExtractorTimeoutFixture>
{
}

/// <summary>
/// Fixture for ExtractorTimeout tests - ensures clean state.
/// </summary>
public class ExtractorTimeoutFixture : IDisposable
{
    public void Dispose()
    {
        ExtractorTimeout.Reset();
    }
}
