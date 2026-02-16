// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using ApiExtractor.Go;
using Xunit;

namespace ApiExtractor.Tests;

/// <summary>
/// Tests that REQUIRE compiled analysis to pass.
/// These document the gap between source-only parsing and compiled-artifact
/// analysis for Go's external dependency type resolution and method promotion.
///
/// The Go source parser (go/parser) is purely syntactic — it creates AST nodes
/// for external type references like httputil.RoundTripper but cannot:
/// 1. Classify them as interface vs struct (all go into Types[], not Structs/Interfaces)
/// 2. Enumerate promoted methods from embedded external types
/// 3. Determine interface satisfaction through structural typing
///
/// A compiled extractor loading the package's export data would resolve all of these.
/// </summary>
public class GoCompiledPrecisionTests : IClassFixture<GoCompiledFixture>
{
    private readonly GoCompiledFixture _fixture;

    public GoCompiledPrecisionTests(GoCompiledFixture fixture)
    {
        _fixture = fixture;
    }

    private ApiIndex GetApi()
    {
        if (_fixture.SkipReason != null) Assert.Skip(_fixture.SkipReason);
        return _fixture.Api!;
    }

    /// <summary>
    /// The Go extractor uses go/parser which is purely syntactic. When it encounters
    /// httputil.RoundTripper, httputil.TransportConfig, and httputil.Handler, it records
    /// the type references but has no way to classify them. All external types are placed
    /// in the dependency's Types[] (unclassified).
    ///
    /// A compiled extractor would load the package's export data and correctly classify
    /// RoundTripper and Handler as interfaces, and TransportConfig as a struct.
    /// </summary>
    [Fact(Skip = "Requires compiled artifacts")]
    [Trait("Category", "CompiledOnly")]
    public void SourceParser_CannotClassify_ExternalDependencyTypes()
    {
        var api = GetApi();

        var dep = api.Dependencies?.FirstOrDefault(d =>
            d.Package.Contains("httputil", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(dep);

        // RoundTripper is an interface in the external package (named after
        // the net/http.RoundTripper interface pattern). The source parser
        // cannot determine this — it puts all external types in Types[].
        // A compiled extractor reading the package's export data would
        // classify it correctly in Interfaces[].
        Assert.NotNull(dep.Interfaces);
        Assert.Contains(dep.Interfaces, i => i.Name == "RoundTripper");

        // TransportConfig is a struct in the external package.
        // Source parser also puts it in Types[] (unclassified).
        Assert.NotNull(dep.Structs);
        Assert.Contains(dep.Structs, s => s.Name == "TransportConfig");
    }

    /// <summary>
    /// ServiceTransport embeds httputil.RoundTripper. In Go, embedding promotes
    /// the embedded type's methods to the outer struct. The source parser cannot
    /// enumerate RoundTripper's methods (it doesn't have the package source),
    /// so ServiceTransport appears to have no methods.
    ///
    /// A compiled extractor loading httputil's export data would see RoundTripper's
    /// method set and promote those methods to ServiceTransport.
    /// </summary>
    [Fact(Skip = "Requires compiled artifacts")]
    [Trait("Category", "CompiledOnly")]
    public void SourceParser_CannotEnumerate_PromotedMethods_FromExternalEmbed()
    {
        var api = GetApi();
        var structs = api.Packages?.SelectMany(p => p.Structs ?? []).ToList();

        var transport = structs?.FirstOrDefault(s => s.Name == "ServiceTransport");
        Assert.NotNull(transport);

        // The struct records the embedded type
        Assert.NotNull(transport.Embeds);
        Assert.Contains(transport.Embeds, e =>
            e.Contains("RoundTripper", StringComparison.Ordinal));

        // But ServiceTransport has no locally-defined methods, only the embedding.
        // The Go extractor associates constructor functions (New* returning this type)
        // as struct methods, but cannot enumerate the ACTUAL promoted methods
        // from the external RoundTripper interface.
        // A compiled extractor would promote RoundTripper's methods (e.g., RoundTrip)
        // to ServiceTransport, in addition to any constructors.
        var promotedMethods = (transport.Methods ?? [])
            .Where(m => !m.Name.StartsWith("New", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(promotedMethods);
    }

    /// <summary>
    /// Client embeds *http.Client, which promotes all of http.Client's exported
    /// methods (Do, Get, Head, Post, PostForm, CloseIdleConnections) to Client.
    /// The source parser records the embedding but cannot enumerate the promoted
    /// methods because it doesn't type-check or load the net/http package.
    ///
    /// A compiled extractor using go/types would resolve the full method set
    /// including promoted methods from the embedded stdlib type.
    /// </summary>
    [Fact(Skip = "Requires compiled artifacts")]
    [Trait("Category", "CompiledOnly")]
    public void SourceParser_CannotEnumerate_PromotedMethods_FromStdlibEmbed()
    {
        var api = GetApi();
        var structs = api.Packages?.SelectMany(p => p.Structs ?? []).ToList();

        var client = structs?.FirstOrDefault(s => s.Name == "Client");
        Assert.NotNull(client);

        // Client has locally-defined methods
        var methods = client.Methods?.Select(m => m.Name).ToList() ?? [];
        Assert.Contains("GetResource", methods);
        Assert.Contains("ListResources", methods);

        // Client also embeds *http.Client, which provides Do(), Get(), etc.
        // The source parser cannot enumerate these promoted methods.
        // A compiled extractor would include them in the method set.
        Assert.Contains("Do", methods);
    }
}
