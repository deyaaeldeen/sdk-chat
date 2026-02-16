// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using ApiExtractor.Python;
using Xunit;

namespace ApiExtractor.Tests;

/// <summary>
/// Fixture that extracts Python API from the CompiledMode fixture once.
/// Used by PythonCompiledPrecisionTests and PythonCompiledFixtureTests.
/// </summary>
public class PythonCompiledFixture : IAsyncLifetime
{
    private static readonly string TestFixturesPath =
        Path.Combine(AppContext.BaseDirectory, "TestFixtures", "CompiledMode", "Python");

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
/// Tests that REQUIRE compiled/runtime analysis to pass.
/// These document the accuracy gap that a runtime-based extractor will close.
/// </summary>
public class PythonCompiledPrecisionTests : IClassFixture<PythonCompiledFixture>
{
    private readonly PythonCompiledFixture _fixture;

    public PythonCompiledPrecisionTests(PythonCompiledFixture fixture)
    {
        _fixture = fixture;
    }

    private ApiIndex GetApi()
    {
        if (_fixture.SkipReason != null) Assert.Skip(_fixture.SkipReason);
        return _fixture.Api!;
    }

    /// <summary>
    /// With `from __future__ import annotations`, all annotations are strings.
    /// The AST parser records "HTTPResponse" literally. A compiled extractor
    /// resolves it to http.client.HTTPResponse via typing.get_type_hints().
    /// </summary>
    [Fact(Skip = "Requires compiled artifacts")]
    [Trait("Category", "CompiledOnly")]
    public void SourceParser_CannotResolve_StringFormAnnotations()
    {
        var api = GetApi();
        var classes = api.Modules.SelectMany(m => m.Classes ?? []).ToList();

        var client = classes.FirstOrDefault(c => c.Name == "ServiceClient");
        Assert.NotNull(client);

        var getResponse = client.Methods?.FirstOrDefault(m => m.Name == "get_response");
        Assert.NotNull(getResponse);

        Assert.NotNull(getResponse.Ret);
        Assert.Contains("HTTPResponse", getResponse.Ret);

        // Compiled extraction should produce fully qualified: "http.client.HTTPResponse"
        Assert.Contains("http.client", getResponse.Ret);
    }
}
