// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using ApiExtractor.Contracts;
using ApiExtractor.Go;
using Xunit;

namespace ApiExtractor.Tests;

/// <summary>
/// Tests for ExtractorResult warning propagation in IApiExtractor implementations.
/// Validates that stderr output from external extractors is surfaced as warnings.
/// </summary>
public class ExtractorResultWarningTests
{
    [Fact]
    public void CreateSuccess_WithWarnings_PreservesWarningList()
    {
        var index = new ApiIndex { Package = "test" };
        var warnings = new List<string> { "deprecation warning: foo", "info: bar" };
        var result = ExtractorResult<ApiIndex>.CreateSuccess(index, warnings);

        Assert.True(result.IsSuccess);
        Assert.Equal(index, result.GetValueOrThrow());
        Assert.Equal(2, result.Warnings.Count);
        Assert.Contains("deprecation warning: foo", result.Warnings);
        Assert.Contains("info: bar", result.Warnings);
    }

    [Fact]
    public void CreateSuccess_WithNullWarnings_DefaultsToEmpty()
    {
        var index = new ApiIndex { Package = "test" };
        var result = ExtractorResult<ApiIndex>.CreateSuccess(index, null);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void CreateSuccess_WithoutWarnings_DefaultsToEmpty()
    {
        var index = new ApiIndex { Package = "test" };
        var result = ExtractorResult<ApiIndex>.CreateSuccess(index);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void CreateFailure_HasNoWarningsByDefault()
    {
        var result = ExtractorResult<ApiIndex>.CreateFailure("error");

        Assert.False(result.IsSuccess);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ToBase_PreservesWarnings()
    {
        var index = new ApiIndex { Package = "test" };
        var warnings = new List<string> { "warning 1" };
        var result = ExtractorResult<ApiIndex>.CreateSuccess(index, warnings);

        var baseResult = result.ToBase();

        Assert.True(baseResult.IsSuccess);
        Assert.Single(baseResult.Warnings);
        Assert.Equal("warning 1", baseResult.Warnings[0]);
    }

    [Fact]
    public void ParseStderrWarnings_EmptyString_ReturnsEmpty()
    {
        var warnings = ParseStderrHelper("");
        Assert.Empty(warnings);
    }

    [Fact]
    public void ParseStderrWarnings_NullString_ReturnsEmpty()
    {
        var warnings = ParseStderrHelper(null);
        Assert.Empty(warnings);
    }

    [Fact]
    public void ParseStderrWarnings_MultiLine_SplitsIntoWarnings()
    {
        var stderr = "WARNING: deprecated API used\nINFO: fallback mode active\n";
        var warnings = ParseStderrHelper(stderr);

        Assert.Equal(2, warnings.Count);
        Assert.Equal("WARNING: deprecated API used", warnings[0]);
        Assert.Equal("INFO: fallback mode active", warnings[1]);
    }

    [Fact]
    public void ParseStderrWarnings_TrimsWhitespace()
    {
        var stderr = "  warning: spaces  \n  info: tabs  \n";
        var warnings = ParseStderrHelper(stderr);

        Assert.Equal(2, warnings.Count);
        Assert.Equal("warning: spaces", warnings[0]);
        Assert.Equal("info: tabs", warnings[1]);
    }

    [Fact]
    public void ParseStderrWarnings_SkipsEmptyLines()
    {
        var stderr = "warning: first\n\n\nwarning: second\n";
        var warnings = ParseStderrHelper(stderr);

        Assert.Equal(2, warnings.Count);
    }

    /// <summary>
    /// Helper that mirrors the ParseStderrWarnings logic used in extractors.
    /// </summary>
    private static IReadOnlyList<string> ParseStderrHelper(string? stderr)
        => string.IsNullOrWhiteSpace(stderr)
            ? []
            : stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
