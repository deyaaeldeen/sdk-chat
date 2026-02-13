// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using ApiExtractor.DotNet;
using ApiExtractor.Go;
using ApiExtractor.Java;
using ApiExtractor.Python;
using ApiExtractor.TypeScript;
using Xunit;
using DotNetApiIndex = ApiExtractor.DotNet.ApiIndex;
using DotNetDependencyInfo = ApiExtractor.DotNet.DependencyInfo;
using DotNetMemberInfo = ApiExtractor.DotNet.MemberInfo;
using DotNetNamespaceInfo = ApiExtractor.DotNet.NamespaceInfo;
using DotNetTypeInfo = ApiExtractor.DotNet.TypeInfo;
using GoApiIndex = ApiExtractor.Go.ApiIndex;
using GoFuncApi = ApiExtractor.Go.FuncApi;
using GoPackageApi = ApiExtractor.Go.PackageApi;
using GoStructApi = ApiExtractor.Go.StructApi;
using JavaApiIndex = ApiExtractor.Java.ApiIndex;
using JavaClassInfo = ApiExtractor.Java.ClassInfo;
using JavaMethodInfo = ApiExtractor.Java.MethodInfo;
using JavaPackageInfo = ApiExtractor.Java.PackageInfo;
using PyApiIndex = ApiExtractor.Python.ApiIndex;
using PyClassInfo = ApiExtractor.Python.ClassInfo;
using PyModuleInfo = ApiExtractor.Python.ModuleInfo;
using TsApiIndex = ApiExtractor.TypeScript.ApiIndex;
using TsClassInfo = ApiExtractor.TypeScript.ClassInfo;
using TsDependencyInfo = ApiExtractor.TypeScript.DependencyInfo;
using TsFunctionInfo = ApiExtractor.TypeScript.FunctionInfo;
using TsInterfaceInfo = ApiExtractor.TypeScript.InterfaceInfo;
using TsModuleInfo = ApiExtractor.TypeScript.ModuleInfo;

namespace ApiExtractor.Tests;

/// <summary>
/// Tests that stdlib and builtin types are correctly excluded from dependencies
/// by the dynamic detection logic in each language extractor.
/// .NET: Roslyn assembly metadata
/// TypeScript: ts-morph type resolution
/// Go: go/types.Universe + GOROOT/src scan
/// Java: ModuleLayer.boot() + JRT filesystem
/// Python: sys.stdlib_module_names
/// </summary>
public class StdlibDetectionTests
{
    private static readonly string TestFixturesBase = Path.Combine(AppContext.BaseDirectory, "TestFixtures");

    // =========================================================================
    // Go — Integration Tests
    // =========================================================================

    /// <summary>
    /// The Go test fixture imports context and time (stdlib packages).
    /// Verify the extractor does not report them as external dependencies.
    /// </summary>
    [Fact]
    public async Task Go_Extract_StdlibPackages_NotInDependencies()
    {
        var extractor = new GoApiExtractor();
        if (!extractor.IsAvailable()) Assert.Skip(extractor.UnavailableReason ?? "Go not available");

        var api = await extractor.ExtractAsync(Path.Combine(TestFixturesBase, "Go"));

        Assert.NotNull(api);

        // The fixture uses context.Context and time.Time — both stdlib.
        // They must not appear in the dependencies list.
        if (api.Dependencies is { Count: > 0 })
        {
            var depPackages = api.Dependencies.Select(d => d.Package).ToList();
            Assert.DoesNotContain("context", depPackages);
            Assert.DoesNotContain("time", depPackages);
            Assert.DoesNotContain("fmt", depPackages);
            Assert.DoesNotContain("io", depPackages);

            // Stdlib packages never contain a dot in the first path element
            foreach (var pkg in depPackages)
            {
                var firstElement = pkg.Contains('/') ? pkg[..pkg.IndexOf('/')] : pkg;
                Assert.Contains('.', firstElement); // external packages have domain-based paths
            }
        }
    }

    // =========================================================================
    // Go — Unit Tests (in-memory, no runtime needed)
    // =========================================================================

    [Theory]
    [InlineData("context.Context")]
    [InlineData("time.Time")]
    [InlineData("time.Duration")]
    [InlineData("io.Reader")]
    [InlineData("io.Writer")]
    [InlineData("net.Conn")]
    [InlineData("http.Request")]
    [InlineData("os.File")]
    [InlineData("sync.Mutex")]
    [InlineData("fmt.Stringer")]
    public void Go_StdlibQualifiedTypes_ExcludedFromStubs(string stdlibType)
    {
        // A method returning a stdlib-qualified type should not create a
        // "Dependency Types" section in the stubs output.
        var api = new GoApiIndex
        {
            Package = "test",
            Packages =
            [
                new GoPackageApi
                {
                    Name = "test",
                    Structs =
                    [
                        new GoStructApi
                        {
                            Name = "Client",
                            Methods =
                            [
                                new GoFuncApi { Name = "Do", Sig = "ctx context.Context", Ret = stdlibType }
                            ]
                        }
                    ]
                }
            ]
        };

        var stubs = api.ToStubs();
        Assert.NotNull(stubs);
        // The stdlib package alias (e.g., "time" from "time.Time") must not appear
        // as a dependency header "// From: time"
        var pkgAlias = stdlibType.Split('.')[0];
        Assert.DoesNotContain($"// From: {pkgAlias}", stubs);
    }

    [Theory]
    [InlineData("bool")]
    [InlineData("int")]
    [InlineData("int64")]
    [InlineData("float64")]
    [InlineData("string")]
    [InlineData("error")]
    [InlineData("any")]
    [InlineData("byte")]
    [InlineData("rune")]
    [InlineData("uint")]
    [InlineData("uintptr")]
    [InlineData("complex128")]
    public void Go_BuiltinPrimitives_ExcludedFromStubs(string builtinType)
    {
        var api = new GoApiIndex
        {
            Package = "test",
            Packages =
            [
                new GoPackageApi
                {
                    Name = "test",
                    Structs =
                    [
                        new GoStructApi
                        {
                            Name = "Client",
                            Methods =
                            [
                                new GoFuncApi { Name = "Do", Ret = builtinType }
                            ]
                        }
                    ]
                }
            ]
        };

        var stubs = api.ToStubs();
        Assert.NotNull(stubs);
        Assert.DoesNotContain("Dependency Types", stubs);
    }

    [Fact]
    public void Go_ExternalPackage_IncludedInStubs()
    {
        // Verify that non-stdlib packages DO appear in dependencies.
        var api = new GoApiIndex
        {
            Package = "test",
            Packages =
            [
                new GoPackageApi
                {
                    Name = "test",
                    Structs =
                    [
                        new GoStructApi { Name = "Client" }
                    ]
                }
            ],
            Dependencies =
            [
                new Go.DependencyInfo
                {
                    Package = "github.com/Azure/azure-sdk-for-go/sdk/azcore",
                    Structs = [new GoStructApi { Name = "Policy" }]
                }
            ]
        };

        var stubs = api.ToStubs();
        Assert.Contains("Dependency Types", stubs);
        Assert.Contains("azcore", stubs);
    }

    // =========================================================================
    // Java — Integration Tests
    // =========================================================================

    /// <summary>
    /// The Java test fixture imports java.time, java.util, and java.util.concurrent.
    /// Verify these don't appear in extracted dependencies.
    /// </summary>
    [Fact]
    public async Task Java_Extract_StdlibPackages_NotInDependencies()
    {
        var extractor = new JavaApiExtractor();
        if (!extractor.IsAvailable()) Assert.Skip(extractor.UnavailableReason ?? "Java not available");

        var api = await extractor.ExtractAsync(Path.Combine(TestFixturesBase, "Java"));

        Assert.NotNull(api);

        if (api.Dependencies is { Count: > 0 })
        {
            var depPackages = api.Dependencies.Select(d => d.Package).ToList();
            // java.* packages must never be external dependencies
            Assert.DoesNotContain(depPackages, p => p.StartsWith("java.", StringComparison.Ordinal));
            Assert.DoesNotContain(depPackages, p => p.StartsWith("javax.", StringComparison.Ordinal));
            Assert.DoesNotContain(depPackages, p => p.StartsWith("jdk.", StringComparison.Ordinal));
            Assert.DoesNotContain(depPackages, p => p.StartsWith("sun.", StringComparison.Ordinal));
        }
    }

    // =========================================================================
    // Java — Unit Tests (in-memory, no runtime needed)
    // =========================================================================

    [Theory]
    [InlineData("String")]
    [InlineData("Integer")]
    [InlineData("Long")]
    [InlineData("Boolean")]
    [InlineData("Object")]
    [InlineData("List")]
    [InlineData("Map")]
    [InlineData("Set")]
    [InlineData("Optional")]
    [InlineData("CompletableFuture")]
    [InlineData("InputStream")]
    [InlineData("OutputStream")]
    [InlineData("Throwable")]
    [InlineData("Exception")]
    [InlineData("Iterable")]
    [InlineData("Iterator")]
    public void Java_BuiltinSimpleNames_ExcludedFromStubs(string builtinType)
    {
        var api = new JavaApiIndex
        {
            Package = "test",
            Packages =
            [
                new JavaPackageInfo
                {
                    Name = "test",
                    Classes =
                    [
                        new JavaClassInfo
                        {
                            Name = "Client",
                            Methods =
                            [
                                new JavaMethodInfo { Name = "doWork", Ret = builtinType }
                            ]
                        }
                    ]
                }
            ]
        };

        var stubs = api.ToStubs();
        Assert.NotNull(stubs);
        // Builtin-only return types should not produce a dependency section
        Assert.DoesNotContain("Dependency Types", stubs);
    }

    [Theory]
    [InlineData("Map<String, List<Integer>>")]
    [InlineData("CompletableFuture<Void>")]
    [InlineData("Optional<String>")]
    [InlineData("List<Map<String, Object>>")]
    public void Java_GenericBuiltinCombinations_ExcludedFromStubs(string genericType)
    {
        var api = new JavaApiIndex
        {
            Package = "test",
            Packages =
            [
                new JavaPackageInfo
                {
                    Name = "test",
                    Classes =
                    [
                        new JavaClassInfo
                        {
                            Name = "Client",
                            Methods =
                            [
                                new JavaMethodInfo { Name = "getData", Ret = genericType }
                            ]
                        }
                    ]
                }
            ]
        };

        var stubs = api.ToStubs();
        Assert.NotNull(stubs);
        Assert.DoesNotContain("Dependency Types", stubs);
    }

    [Theory]
    [InlineData("int")]
    [InlineData("long")]
    [InlineData("double")]
    [InlineData("boolean")]
    [InlineData("char")]
    [InlineData("byte")]
    [InlineData("float")]
    [InlineData("short")]
    [InlineData("void")]
    public void Java_Primitives_ExcludedFromStubs(string primitive)
    {
        var api = new JavaApiIndex
        {
            Package = "test",
            Packages =
            [
                new JavaPackageInfo
                {
                    Name = "test",
                    Classes =
                    [
                        new JavaClassInfo
                        {
                            Name = "Client",
                            Methods =
                            [
                                new JavaMethodInfo { Name = "compute", Ret = primitive }
                            ]
                        }
                    ]
                }
            ]
        };

        var stubs = api.ToStubs();
        Assert.NotNull(stubs);
        Assert.DoesNotContain("Dependency Types", stubs);
    }

    [Fact]
    public void Java_ExternalPackage_IncludedInStubs()
    {
        var api = new JavaApiIndex
        {
            Package = "test",
            Packages =
            [
                new JavaPackageInfo
                {
                    Name = "test",
                    Classes = [new JavaClassInfo { Name = "Client" }]
                }
            ],
            Dependencies =
            [
                new Java.DependencyInfo
                {
                    Package = "com.azure.core",
                    Classes = [new JavaClassInfo { Name = "HttpPipeline" }]
                }
            ]
        };

        var stubs = api.ToStubs();
        Assert.Contains("Dependency Types", stubs);
        Assert.Contains("com.azure.core", stubs);
    }

    // =========================================================================
    // Python — Integration Tests
    // =========================================================================

    /// <summary>
    /// The Python test fixture imports typing, datetime, enum, and dataclasses.
    /// Verify these don't appear in extracted dependencies.
    /// </summary>
    [Fact]
    public async Task Python_Extract_StdlibPackages_NotInDependencies()
    {
        var extractor = new PythonApiExtractor();
        if (!extractor.IsAvailable()) Assert.Skip(extractor.UnavailableReason ?? "Python not available");

        var api = await extractor.ExtractAsync(Path.Combine(TestFixturesBase, "Python"));

        Assert.NotNull(api);

        if (api.Dependencies is { Count: > 0 })
        {
            var depPackages = api.Dependencies.Select(d => d.Package).ToList();
            // These are all stdlib packages — must not be in dependencies
            Assert.DoesNotContain("typing", depPackages);
            Assert.DoesNotContain("datetime", depPackages);
            Assert.DoesNotContain("enum", depPackages);
            Assert.DoesNotContain("dataclasses", depPackages);
            Assert.DoesNotContain("collections", depPackages);
            Assert.DoesNotContain("abc", depPackages);
            Assert.DoesNotContain("os", depPackages);
            Assert.DoesNotContain("sys", depPackages);
            Assert.DoesNotContain("json", depPackages);
            Assert.DoesNotContain("io", depPackages);
        }
    }

    // =========================================================================
    // Python — Unit Tests (in-memory, no runtime needed)
    // =========================================================================

    [Fact]
    public void Python_StdlibModules_NotInFormattedStubs()
    {
        // Create an API that references only stdlib-qualified types
        var api = new PyApiIndex(
            Package: "test-pkg",
            Modules:
            [
                new PyModuleInfo("client",
                    [
                        new PyClassInfo
                        {
                            Name = "Client",
                            Methods =
                            [
                                new Python.MethodInfo { Name = "get", Signature = "(self)", Ret = "Optional[str]" },
                                new Python.MethodInfo { Name = "list", Signature = "(self)", Ret = "List[Dict[str, Any]]" },
                                new Python.MethodInfo { Name = "created_at", Signature = "(self)", Ret = "datetime.datetime" }
                            ]
                        }
                    ],
                    null)
            ]
        );

        var stubs = api.ToStubs();
        Assert.NotNull(stubs);
        Assert.DoesNotContain("Dependency Types", stubs);
    }

    [Fact]
    public void Python_ExternalPackage_IncludedInStubs()
    {
        var api = new PyApiIndex(
            Package: "test-pkg",
            Modules:
            [
                new PyModuleInfo("client",
                    [new PyClassInfo { Name = "Client" }],
                    null)
            ],
            Dependencies:
            [
                new Python.DependencyInfo
                {
                    Package = "azure-core",
                    Classes = [new PyClassInfo { Name = "PipelinePolicy" }]
                }
            ]
        );

        var stubs = api.ToStubs();
        Assert.Contains("Dependency Types", stubs);
        Assert.Contains("azure-core", stubs);
    }

    // =========================================================================
    // .NET — Integration Tests
    // =========================================================================

    /// <summary>
    /// The .NET test fixture uses Task, CancellationToken, string, IAsyncEnumerable, etc.
    /// Verify these System types don't appear as external dependencies.
    /// </summary>
    [Fact]
    public async Task DotNet_Extract_StdlibTypes_NotInDependencies()
    {
        var extractor = new CSharpApiExtractor();
        var api = await extractor.ExtractAsync(Path.Combine(TestFixturesBase, "DotNet"));

        Assert.NotNull(api);

        if (api.Dependencies is { Count: > 0 })
        {
            var depPackages = api.Dependencies.Select(d => d.Package).ToList();
            // System namespaces must never appear as external dependencies
            Assert.DoesNotContain(depPackages, p => p.StartsWith("System", StringComparison.Ordinal));
            Assert.DoesNotContain(depPackages, p => p.StartsWith("Microsoft.Extensions", StringComparison.Ordinal));
        }
    }

    // =========================================================================
    // .NET — Unit Tests (in-memory, no runtime needed)
    // =========================================================================

    [Theory]
    [InlineData("Task<string> GetAsync(CancellationToken ct)")]
    [InlineData("IAsyncEnumerable<int> ListAsync()")]
    [InlineData("ValueTask<bool> TryGetAsync(string id)")]
    [InlineData("Dictionary<string, List<int>> GetAll()")]
    [InlineData("IReadOnlyList<string> GetNames()")]
    [InlineData("Action<EventArgs> Handler { get; }")]
    [InlineData("Func<string, Task<bool>> Predicate { get; }")]
    [InlineData("Memory<byte> GetBuffer()")]
    [InlineData("ReadOnlySpan<char> GetSpan()")]
    [InlineData("DateTimeOffset CreatedAt { get; }")]
    [InlineData("TimeSpan Timeout { get; }")]
    [InlineData("Uri Endpoint { get; }")]
    [InlineData("Guid Id { get; }")]
    [InlineData("Stream Content { get; }")]
    public void DotNet_BuiltinSignatures_ExcludedFromStubs(string signature)
    {
        var api = new DotNetApiIndex
        {
            Package = "Test",
            Namespaces =
            [
                new DotNetNamespaceInfo
                {
                    Name = "Test",
                    Types =
                    [
                        new DotNetTypeInfo
                        {
                            Name = "TestClient",
                            Kind = "class",
                            Members = [new DotNetMemberInfo { Name = "Member", Kind = "method", Signature = signature }]
                        }
                    ]
                }
            ]
        };

        var stubs = api.ToStubs();
        Assert.NotNull(stubs);
        Assert.DoesNotContain("// From: System", stubs);
        Assert.DoesNotContain("Dependency Types", stubs);
    }

    [Theory]
    [InlineData("string")]
    [InlineData("int")]
    [InlineData("bool")]
    [InlineData("long")]
    [InlineData("double")]
    [InlineData("float")]
    [InlineData("decimal")]
    [InlineData("byte")]
    [InlineData("char")]
    [InlineData("object")]
    [InlineData("void")]
    public void DotNet_PrimitiveAliases_ExcludedFromStubs(string primitive)
    {
        var api = new DotNetApiIndex
        {
            Package = "Test",
            Namespaces =
            [
                new DotNetNamespaceInfo
                {
                    Name = "Test",
                    Types =
                    [
                        new DotNetTypeInfo
                        {
                            Name = "TestClient",
                            Kind = "class",
                            Members = [new DotNetMemberInfo { Name = "Get", Kind = "method", Signature = $"{primitive} Get()" }]
                        }
                    ]
                }
            ]
        };

        var stubs = api.ToStubs();
        Assert.NotNull(stubs);
        Assert.DoesNotContain("Dependency Types", stubs);
    }

    [Fact]
    public void DotNet_ExternalPackage_IncludedInStubs()
    {
        var api = new DotNetApiIndex
        {
            Package = "Test",
            Namespaces =
            [
                new DotNetNamespaceInfo
                {
                    Name = "Test",
                    Types = [new DotNetTypeInfo { Name = "Client", Kind = "class" }]
                }
            ],
            Dependencies =
            [
                new DotNetDependencyInfo
                {
                    Package = "Azure.Core",
                    Types = [new DotNetTypeInfo { Name = "Response", Kind = "class" }]
                }
            ]
        };

        var stubs = api.ToStubs();
        Assert.Contains("Dependency Types", stubs);
        Assert.Contains("Azure.Core", stubs);
    }

    // =========================================================================
    // TypeScript — Integration Tests
    // =========================================================================

    /// <summary>
    /// The TypeScript test fixture uses Promise, string, number, Record, etc.
    /// Verify these built-in types don't appear as external dependencies.
    /// </summary>
    [Fact]
    public async Task TypeScript_Extract_BuiltinTypes_NotInDependencies()
    {
        var extractor = new TypeScriptApiExtractor();
        if (!extractor.IsAvailable()) Assert.Skip(extractor.UnavailableReason ?? "TypeScript not available");

        var api = await extractor.ExtractAsync(Path.Combine(TestFixturesBase, "TypeScript"));

        Assert.NotNull(api);

        if (api.Dependencies is { Count: > 0 })
        {
            var depPackages = api.Dependencies.Select(d => d.Package).ToList();
            // Built-in globals must not be listed as dependencies
            Assert.DoesNotContain("Promise", depPackages);
            Assert.DoesNotContain("Record", depPackages);
            Assert.DoesNotContain("Map", depPackages);
            Assert.DoesNotContain("Set", depPackages);
            Assert.DoesNotContain("Array", depPackages);
            Assert.DoesNotContain("Error", depPackages);
        }
    }

    // =========================================================================
    // TypeScript — Unit Tests (in-memory, no runtime needed)
    // =========================================================================

    [Theory]
    [InlineData("(id: string) => Promise<void>")]
    [InlineData("(ids: string[]) => Promise<Record<string, number>>")]
    [InlineData("(items: Array<string>) => Map<string, number>")]
    [InlineData("(data: Uint8Array) => ArrayBuffer")]
    [InlineData("(signal: AbortSignal) => Promise<boolean>")]
    [InlineData("() => AsyncIterable<string>")]
    [InlineData("(err: Error) => void")]
    [InlineData("() => Date")]
    [InlineData("() => RegExp")]
    [InlineData("() => Set<string>")]
    public void TypeScript_BuiltinSignatures_ExcludedFromStubs(string signature)
    {
        var api = new TsApiIndex
        {
            Package = "test",
            Modules =
            [
                new TsModuleInfo
                {
                    Name = "test",
                    Functions =
                    [
                        new TsFunctionInfo { Name = "doWork", ExportPath = ".", Sig = signature }
                    ]
                }
            ]
        };

        var stubs = api.ToStubs();
        Assert.NotNull(stubs);
        Assert.DoesNotContain("Dependencies", stubs);
    }

    [Theory]
    [InlineData("string")]
    [InlineData("number")]
    [InlineData("boolean")]
    [InlineData("void")]
    [InlineData("undefined")]
    [InlineData("null")]
    [InlineData("any")]
    [InlineData("unknown")]
    [InlineData("never")]
    [InlineData("bigint")]
    [InlineData("symbol")]
    [InlineData("object")]
    public void TypeScript_PrimitiveTypes_ExcludedFromStubs(string primitive)
    {
        var api = new TsApiIndex
        {
            Package = "test",
            Modules =
            [
                new TsModuleInfo
                {
                    Name = "test",
                    Functions =
                    [
                        new TsFunctionInfo { Name = "get", ExportPath = ".", Sig = $"() => {primitive}" }
                    ]
                }
            ]
        };

        var stubs = api.ToStubs();
        Assert.NotNull(stubs);
        Assert.DoesNotContain("Dependencies", stubs);
    }

    [Fact]
    public void TypeScript_ExternalPackage_IncludedInStubs()
    {
        var api = new TsApiIndex
        {
            Package = "test",
            Modules =
            [
                new TsModuleInfo
                {
                    Name = "test",
                    Classes = [new TsClassInfo { Name = "Client", ExportPath = "." }]
                }
            ],
            Dependencies =
            [
                new TsDependencyInfo
                {
                    Package = "@azure/core-rest-pipeline",
                    Interfaces = [new TsInterfaceInfo { Name = "PipelinePolicy" }]
                }
            ]
        };

        var stubs = api.ToStubs();
        Assert.Contains("Dependencies", stubs);
        Assert.Contains("@azure/core-rest-pipeline", stubs);
    }
}
