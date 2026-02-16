// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using ApiExtractor.Java;
using Xunit;

namespace ApiExtractor.Tests;

/// <summary>
/// Tests that REQUIRE compiled analysis to pass.
/// These document the gap between source-only parsing and compiled-artifact
/// analysis for Java's external dependency type resolution.
///
/// The Java source parser (JavaParser) resolves type references via import
/// statements and can classify types seen in implements clauses as interfaces.
/// However, it cannot:
/// 1. Classify types seen only in parameter/return positions (defaults to "class")
/// 2. Determine if an extends target is abstract, final, or concrete
/// 3. Enumerate members of external types from their JARs
///
/// A compiled extractor using reflection or ASM on the dependency JARs would
/// resolve all of these.
/// </summary>
public class JavaCompiledPrecisionTests : IClassFixture<JavaCompiledFixture>
{
    private readonly JavaCompiledFixture _fixture;

    public JavaCompiledPrecisionTests(JavaCompiledFixture fixture)
    {
        _fixture = fixture;
    }

    private ApiIndex GetApi()
    {
        if (_fixture.SkipReason != null) Assert.Skip(_fixture.SkipReason);
        return _fixture.Api!;
    }

    /// <summary>
    /// HttpRequest and HttpResponse appear only as method parameter/return types.
    /// The Java source parser classifies types in implements clauses as interfaces,
    /// but types in parameter/return positions have no syntactic hint — the parser
    /// defaults them to the Classes[] bucket.
    ///
    /// In the external package, HttpRequest and HttpResponse are interfaces
    /// (following Java's common HTTP abstraction pattern). A compiled extractor
    /// using reflection would correctly classify them.
    /// </summary>
    [Fact(Skip = "Requires compiled artifacts")]
    [Trait("Category", "CompiledOnly")]
    public void SourceParser_CannotClassify_ExternalParameterTypes()
    {
        var api = GetApi();

        var dep = api.Dependencies?.FirstOrDefault(d =>
            d.Package.Contains("somelib", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(dep);

        // HttpRequest is defined as an interface in the external package.
        // The source parser defaults it to Classes[] because it only appears
        // as a parameter type, not in an implements clause.
        // A compiled extractor loading the JAR would use reflection to
        // determine the actual kind.
        Assert.NotNull(dep.Interfaces);
        Assert.Contains(dep.Interfaces, i => i.Name == "HttpRequest");
    }

    /// <summary>
    /// ExtendedClient extends HttpClient from the external package.
    /// The source parser tracks the dependency and puts HttpClient in Classes[],
    /// but creates a minimal entry without kind details or member information.
    ///
    /// A compiled extractor using reflection or ASM would determine:
    /// - HttpClient is an abstract class (with abstract methods to override)
    /// - Its public method set (inherited by ExtendedClient)
    /// - Its generic type parameters and bounds (if any)
    /// </summary>
    [Fact(Skip = "Requires compiled artifacts")]
    [Trait("Category", "CompiledOnly")]
    public void SourceParser_CannotDetermine_ExternalSuperclassDetails()
    {
        var api = GetApi();

        var dep = api.Dependencies?.FirstOrDefault(d =>
            d.Package.Contains("somelib", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(dep);

        // HttpClient should be tracked in the dependency
        Assert.NotNull(dep.Classes);
        var httpClient = dep.Classes.FirstOrDefault(c => c.Name == "HttpClient");
        Assert.NotNull(httpClient);

        // The source parser cannot determine that HttpClient is abstract.
        // A compiled extractor would use reflection: Modifier.isAbstract(cls.getModifiers())
        Assert.NotNull(httpClient.Kind);
        Assert.Contains("abstract", httpClient.Kind, StringComparison.OrdinalIgnoreCase);
    }
}
