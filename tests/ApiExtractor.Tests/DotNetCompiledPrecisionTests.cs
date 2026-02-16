// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using ApiExtractor.DotNet;
using Xunit;

namespace ApiExtractor.Tests;

/// <summary>
/// Tests that REQUIRE compiled-artifact analysis to pass.
/// These document the accuracy gap that compiled DLL extraction will close.
/// When the compiled extractor is implemented, update these to use it.
/// </summary>
public class DotNetCompiledPrecisionTests
{
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "TestFixtures", "CompiledMode", "DotNet");

    private readonly CSharpApiExtractor _sourceExtractor = new();

    /// <summary>
    /// JsonElement is a struct (System.ValueType) from System.Text.Json.
    /// The source extractor cannot determine whether an external type is a struct vs class
    /// without loading the containing assembly's metadata. It can only see the type NAME
    /// in the source code.
    ///
    /// A compiled extractor reads the TypeDef/TypeRef table and knows the exact
    /// type kind (struct, class, interface, enum, delegate).
    /// </summary>
    [Fact(Skip = "Requires compiled artifacts")]
    [Trait("Category", "CompiledOnly")]
    public async Task SourceParser_CannotDetermine_ExternalTypeKind()
    {
        var api = await _sourceExtractor.ExtractAsync(FixturePath);
        var types = api.Namespaces.SelectMany(n => n.Types).ToList();

        var client = types.FirstOrDefault(t => t.Name == "JsonServiceClient");
        Assert.NotNull(client);

        var parseMethod = client.Members?.FirstOrDefault(m => m.Name == "ParseToElement");
        Assert.NotNull(parseMethod);
        Assert.NotNull(parseMethod.Signature);
        Assert.Contains("JsonElement", parseMethod.Signature);

        Assert.NotNull(api.Dependencies);
        var stjDep = api.Dependencies.FirstOrDefault(d =>
            d.Package.Contains("System.Text.Json", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(stjDep);
        Assert.NotNull(stjDep.Types);
        var jsonElementType = stjDep.Types.FirstOrDefault(t => t.Name == "JsonElement");
        Assert.NotNull(jsonElementType);
        Assert.Equal("struct", jsonElementType.Kind);
    }
}
