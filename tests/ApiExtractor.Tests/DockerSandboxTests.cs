// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using ApiExtractor.Contracts;
using Xunit;

namespace ApiExtractor.Tests;

/// <summary>
/// Tests for DockerSandbox per-language image configuration.
/// These tests modify environment variables so they must not run in parallel.
/// </summary>
[Collection("DockerSandbox")]
public class DockerSandboxTests : IDisposable
{
    private readonly string? _originalPythonEnv;
    private readonly string? _originalGoEnv;

    public DockerSandboxTests()
    {
        _originalPythonEnv = Environment.GetEnvironmentVariable($"{DockerSandbox.ImageEnvVarPrefix}PYTHON");
        _originalGoEnv = Environment.GetEnvironmentVariable($"{DockerSandbox.ImageEnvVarPrefix}GO");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable($"{DockerSandbox.ImageEnvVarPrefix}PYTHON", _originalPythonEnv);
        Environment.SetEnvironmentVariable($"{DockerSandbox.ImageEnvVarPrefix}GO", _originalGoEnv);
    }

    #region Constants

    [Fact]
    public void ImageEnvVarPrefix_IsExpectedName()
    {
        Assert.Equal("SDK_CHAT_DOCKER_IMAGE_", DockerSandbox.ImageEnvVarPrefix);
    }

    #endregion

    #region GetImageName

    [Fact]
    public void GetImageName_ReturnsDefault_WhenEnvVarNotSet()
    {
        Environment.SetEnvironmentVariable($"{DockerSandbox.ImageEnvVarPrefix}PYTHON", null);

        Assert.Equal("api-extractor-python:latest", DockerSandbox.GetImageName("python"));
    }

    [Fact]
    public void GetImageName_ReturnsDefault_WhenEnvVarIsEmpty()
    {
        Environment.SetEnvironmentVariable($"{DockerSandbox.ImageEnvVarPrefix}GO", "");

        Assert.Equal("api-extractor-go:latest", DockerSandbox.GetImageName("go"));
    }

    [Fact]
    public void GetImageName_ReturnsOverride_WhenEnvVarIsSet()
    {
        Environment.SetEnvironmentVariable($"{DockerSandbox.ImageEnvVarPrefix}PYTHON", "my-registry/python-extractor:v2");

        Assert.Equal("my-registry/python-extractor:v2", DockerSandbox.GetImageName("python"));
    }

    [Theory]
    [InlineData("python", "api-extractor-python:latest")]
    [InlineData("go", "api-extractor-go:latest")]
    [InlineData("java", "api-extractor-java:latest")]
    [InlineData("typescript", "api-extractor-typescript:latest")]
    public void GetImageName_ReturnsCorrectDefault_PerLanguage(string language, string expected)
    {
        // Clear any env overrides
        Environment.SetEnvironmentVariable($"{DockerSandbox.ImageEnvVarPrefix}{language.ToUpperInvariant()}", null);

        Assert.Equal(expected, DockerSandbox.GetImageName(language));
    }

    [Fact]
    public void GetImageName_NormalizesLanguageCase()
    {
        Environment.SetEnvironmentVariable($"{DockerSandbox.ImageEnvVarPrefix}PYTHON", null);

        Assert.Equal("api-extractor-python:latest", DockerSandbox.GetImageName("Python"));
        Assert.Equal("api-extractor-python:latest", DockerSandbox.GetImageName("PYTHON"));
    }

    #endregion
}
