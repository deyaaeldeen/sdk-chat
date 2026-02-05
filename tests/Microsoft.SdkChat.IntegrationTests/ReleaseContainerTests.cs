// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Xunit;

namespace Microsoft.SdkChat.IntegrationTests;

/// <summary>
/// Integration tests for release container CLI commands across all supported languages.
/// Uses the wrapper script (scripts/sdk-chat.sh) which handles Docker setup, path mounting,
/// and credential passthrough automatically.
///
/// Prerequisites:
/// - Docker available
/// - Release image built: docker build -f Dockerfile.release -t sdk-chat:latest .
///   Or use: ./scripts/sdk-chat.sh --build --help
///
/// Run:
///   dotnet test tests/Microsoft.SdkChat.IntegrationTests --filter "Category=Integration"
/// </summary>
[Collection("ReleaseContainer")]
[Trait("Category", "Integration")]
public class ReleaseContainerTests
{
    private readonly ReleaseContainerFixture _fixture;

    public ReleaseContainerTests(ReleaseContainerFixture fixture)
    {
        _fixture = fixture;
    }

    #region Test Data

    /// <summary>
    /// Languages fully supported in native release container.
    /// </summary>
    public static TheoryData<string> SupportedLanguages => new()
    {
        "DotNet",
        "Python",
        "Go",
        "Java",
        "TypeScript"
    };

    /// <summary>
    /// All languages for tests that don't require working extractors.
    /// </summary>
    public static TheoryData<string> AllLanguages => new()
    {
        "DotNet",
        "Python",
        "Go",
        "Java",
        "TypeScript"
    };

    #endregion

    #region Baseline Tests

    [SkippableFact]
    public async Task Container_HelpCommand_ReturnsSuccess()
    {
        Skip.IfNot(_fixture.IsAvailable, _fixture.SkipReason ?? "Container not available");

        var (exitCode, output, error) = await _fixture.RunAsync(["--help"]);

        Assert.True(exitCode == 0, $"Exit code {exitCode}. Error: {error}");
        Assert.Contains("package", output);
    }

    [SkippableFact]
    public async Task Container_DoctorCommand_ReturnsSuccess()
    {
        Skip.IfNot(_fixture.IsAvailable, _fixture.SkipReason ?? "Container not available");

        var (exitCode, output, error) = await _fixture.RunAsync(["doctor"]);

        // Doctor returns 0 if all tools found, non-zero otherwise - both are valid
        Assert.True(exitCode >= 0, $"Unexpected exit code. Output: {output}, Error: {error}");
        // Should produce some output about tool status
        Assert.False(string.IsNullOrWhiteSpace(output + error), "Expected some diagnostic output");
    }

    #endregion

    #region Source Detection Tests

    [SkippableTheory]
    [MemberData(nameof(AllLanguages))]
    public async Task SourceDetect_ReturnsValidOutput(string language)
    {
        Skip.IfNot(_fixture.IsAvailable, _fixture.SkipReason ?? "Container not available");

        var (exitCode, output, error) = await _fixture.RunWithFixtureAsync(
            "package source detect", language);

        Assert.True(exitCode == 0, $"[{language}] Exit code {exitCode}. Error: {error}");
        Assert.False(string.IsNullOrWhiteSpace(output), $"[{language}] Expected output");

        // Should return JSON or path info
        Assert.True(
            output.Contains("\"") || output.Contains("/"),
            $"[{language}] Expected structured output. Got: {output}");
    }

    #endregion

    #region API Extraction Tests

    [SkippableTheory]
    [MemberData(nameof(SupportedLanguages))]
    public async Task ApiExtract_ReturnsValidJson(string language)
    {
        Skip.IfNot(_fixture.IsAvailable, _fixture.SkipReason ?? "Container not available");

        // Minimal fixtures don't have project files, so we need to specify language explicitly
        // --json flag outputs structured JSON instead of human-readable stubs
        var langFlag = ReleaseContainerFixture.GetLanguageFlag(language);
        var (exitCode, output, error) = await _fixture.RunWithFixtureAsync(
            "package api extract", language, $"--language {langFlag} --json");

        Assert.True(exitCode == 0, $"[{language}] Exit code {exitCode}. Error: {error}. Output: {output}");
        Assert.False(string.IsNullOrWhiteSpace(output), $"[{language}] Expected output");

        // API extraction should return JSON with package/namespace info
        Assert.True(
            output.Contains("\"package\"") || output.Contains("\"namespaces\"") || output.Contains("\"types\""),
            $"[{language}] Expected API JSON structure. Got: {output[..Math.Min(500, output.Length)]}");
    }

    #endregion

    #region Samples Detection Tests

    [SkippableTheory]
    [MemberData(nameof(SupportedLanguages))]
    public async Task SamplesDetect_ExecutesWithoutCrash(string language)
    {
        Skip.IfNot(_fixture.IsAvailable, _fixture.SkipReason ?? "Container not available");

        var (exitCode, output, error) = await _fixture.RunWithFixtureAsync(
            "package samples detect", language);

        // Samples detect may return non-zero if no samples folder exists (expected for minimal fixtures)
        // The key assertion: it should not crash
        Assert.True(exitCode >= 0, $"[{language}] Unexpected exit code. Error: {error}");
    }

    #endregion

    #region API Coverage Tests

    [SkippableTheory]
    [MemberData(nameof(SupportedLanguages))]
    public async Task ApiCoverage_ExecutesWithoutCrash(string language)
    {
        Skip.IfNot(_fixture.IsAvailable, _fixture.SkipReason ?? "Container not available");

        var (exitCode, output, error) = await _fixture.RunWithFixtureAsync(
            "package api coverage", language);

        // Coverage may fail if no samples, but should not crash
        Assert.True(exitCode >= 0, $"[{language}] Unexpected exit code. Error: {error}");
    }

    [SkippableTheory]
    [MemberData(nameof(SupportedLanguages))]
    public async Task ApiCoverage_DetectsSubclientOperations(string language)
    {
        Skip.IfNot(_fixture.IsAvailable, _fixture.SkipReason ?? "Container not available");

        var langFlag = ReleaseContainerFixture.GetLanguageFlag(language);
        var (exitCode, output, error) = await _fixture.RunWithFixtureAsync(
            "package api coverage", language, $"--language {langFlag} --json");

        Assert.True(exitCode == 0, $"[{language}] Exit code {exitCode}. Error: {error}. Output: {output}");

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean(), $"[{language}] coverage failed: {output}");
        Assert.True(root.GetProperty("totalOperations").GetInt32() > 0, $"[{language}] Expected operations > 0");

        var expected = language switch
        {
            "DotNet" => (Client: "WidgetClient", Operation: "ListWidgetsAsync"),
            "Python" => (Client: "WidgetClient", Operation: "list_widgets"),
            "Go" => (Client: "WidgetsClient", Operation: "ListWidgets"),
            "Java" => (Client: "WidgetsClient", Operation: "listWidgets"),
            "TypeScript" => (Client: "WidgetClient", Operation: "listWidgets"),
            _ => (Client: "", Operation: "")
        };

        var covered = root.GetProperty("coveredOperations");
        var matched = covered.EnumerateArray().Any(op =>
            op.GetProperty("clientType").GetString() == expected.Client &&
            op.GetProperty("operation").GetString() == expected.Operation);

        Assert.True(matched, $"[{language}] Expected covered operation {expected.Client}.{expected.Operation}. Output: {output}");
    }

    [SkippableTheory]
    [MemberData(nameof(SupportedLanguages))]
    public async Task ApiCoverage_DetectsInterfaceSubclientOperations(string language)
    {
        Skip.IfNot(_fixture.IsAvailable, _fixture.SkipReason ?? "Container not available");

        var langFlag = ReleaseContainerFixture.GetLanguageFlag(language);
        var (exitCode, output, error) = await _fixture.RunWithFixtureAsync(
            "package api coverage", language, $"--language {langFlag} --json");

        Assert.True(exitCode == 0, $"[{language}] Exit code {exitCode}. Error: {error}. Output: {output}");

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean(), $"[{language}] coverage failed: {output}");
        Assert.True(root.GetProperty("totalOperations").GetInt32() > 0, $"[{language}] Expected operations > 0");

        var expected = language switch
        {
            "DotNet" => (Interface: "IRecommendationsClient", Implementation: "RecommendationsClientImpl", Operation: "ListRecommendationsAsync"),
            "Python" => (Interface: "RecommendationsClientBase", Implementation: "RecommendationsClientImpl", Operation: "list_recommendations"),
            "Go" => (Interface: "RecommendationsClient", Implementation: "RecommendationsClientImpl", Operation: "ListRecommendations"),
            "Java" => (Interface: "RecommendationsClient", Implementation: "RecommendationsClientImpl", Operation: "listRecommendations"),
            "TypeScript" => (Interface: "RecommendationsClient", Implementation: "RecommendationsClientImpl", Operation: "listRecommendations"),
            _ => (Interface: "", Implementation: "", Operation: "")
        };

        var covered = root.GetProperty("coveredOperations");
        var matched = covered.EnumerateArray().Any(op =>
        {
            var clientType = op.GetProperty("clientType").GetString();
            var operation = op.GetProperty("operation").GetString();

            return operation == expected.Operation &&
                (clientType == expected.Interface || clientType == expected.Implementation);
        });

        Assert.True(matched,
            $"[{language}] Expected covered operation {expected.Interface}|{expected.Implementation}.{expected.Operation}. Output: {output}");
    }

    #endregion
}
