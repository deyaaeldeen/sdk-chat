// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using ApiExtractor.Contracts;
using ApiExtractor.Python;
using Xunit;

namespace ApiExtractor.Tests;

/// <summary>
/// Tests for formatter fixes across all language extractors.
/// Uses in-memory model construction — no external runtimes required.
/// </summary>
public class FormatterTests
{
    #region Python Formatter

    [Fact]
    public void PythonFormatter_RendersReturnType_ForMethods()
    {
        var api = new Python.ApiIndex("test-pkg",
        [
            new Python.ModuleInfo("mod",
            [
                new Python.ClassInfo
                {
                    Name = "Client",
                    Methods =
                    [
                        new Python.MethodInfo("get_item", "self, id: str", null, false, null, null, "Item")
                    ]
                }
            ], null)
        ]);

        var stubs = Python.PythonFormatter.Format(api);
        Assert.Contains("-> Item", stubs);
        Assert.Contains("def get_item(self, id: str) -> Item: ...", stubs);
    }

    [Fact]
    public void PythonFormatter_RendersReturnType_ForFunctions()
    {
        var api = new Python.ApiIndex("test-pkg",
        [
            new Python.ModuleInfo("mod", null,
            [
                new Python.FunctionInfo
                {
                    Name = "create_client",
                    Signature = "endpoint: str",
                    Ret = "Client",
                    IsAsync = false
                }
            ])
        ]);

        var stubs = Python.PythonFormatter.Format(api);
        Assert.Contains("-> Client", stubs);
        Assert.Contains("def create_client(endpoint: str) -> Client: ...", stubs);
    }

    [Fact]
    public void PythonFormatter_OmitsReturnType_WhenNull()
    {
        var api = new Python.ApiIndex("test-pkg",
        [
            new Python.ModuleInfo("mod",
            [
                new Python.ClassInfo
                {
                    Name = "Client",
                    Methods =
                    [
                        new Python.MethodInfo("close", "self", null, false, null, null, null)
                    ]
                }
            ], null)
        ]);

        var stubs = Python.PythonFormatter.Format(api);
        Assert.Contains("def close(self): ...", stubs);
        Assert.DoesNotContain("->", stubs);
    }

    [Fact]
    public void PythonFormatter_RenderDecorators_BeforeDef()
    {
        var api = new Python.ApiIndex("test-pkg",
        [
            new Python.ModuleInfo("mod",
            [
                new Python.ClassInfo
                {
                    Name = "Client",
                    Methods =
                    [
                        new Python.MethodInfo("from_string", "cls, s: str", "Create from string.", false, true, null, "Client")
                    ]
                }
            ], null)
        ]);

        var stubs = Python.PythonFormatter.Format(api);
        var lines = stubs.Split('\n').Select(l => l.TrimEnd()).ToList();
        var decoratorIdx = lines.FindIndex(l => l.Contains("@classmethod"));
        var defIdx = lines.FindIndex(l => l.Contains("def from_string"));
        Assert.True(decoratorIdx >= 0, "@classmethod decorator not found");
        Assert.True(defIdx >= 0, "def from_string not found");
        Assert.True(decoratorIdx < defIdx, "@classmethod must appear before def");
    }

    [Fact]
    public void PythonFormatter_RenderDoc_AfterDef()
    {
        var api = new Python.ApiIndex("test-pkg",
        [
            new Python.ModuleInfo("mod",
            [
                new Python.ClassInfo
                {
                    Name = "Client",
                    Methods =
                    [
                        new Python.MethodInfo("do_work", "self", "Does work.", false, null, null, null)
                    ]
                }
            ], null)
        ]);

        var stubs = Python.PythonFormatter.Format(api);
        var lines = stubs.Split('\n').Select(l => l.TrimEnd()).ToList();
        var defIdx = lines.FindIndex(l => l.Contains("def do_work"));
        var docIdx = lines.FindIndex(l => l.Contains("\"\"\"Does work.\"\"\""));
        Assert.True(defIdx >= 0, "def do_work not found");
        Assert.True(docIdx >= 0, "docstring not found");
        Assert.True(docIdx > defIdx, "docstring must appear after def");
    }

    [Fact]
    public void PythonFormatter_RendersAsyncDef()
    {
        var api = new Python.ApiIndex("test-pkg",
        [
            new Python.ModuleInfo("mod",
            [
                new Python.ClassInfo
                {
                    Name = "Client",
                    Methods =
                    [
                        new Python.MethodInfo("fetch_async", "self", null, true, null, null, "str")
                    ]
                }
            ], null)
        ]);

        var stubs = Python.PythonFormatter.Format(api);
        Assert.Contains("async def fetch_async(self) -> str: ...", stubs);
    }

    #endregion

    #region Python ConvertToApiIndex (ret + dependencies)

    [Fact]
    public void PythonDeserialization_PreservesReturnType()
    {
        var json = """
        {
            "package": "test-pkg",
            "modules": [{
                "name": "mod",
                "classes": [{
                    "name": "Client",
                    "methods": [{
                        "name": "get_item",
                        "sig": "self, id: str",
                        "ret": "Item",
                        "async": false
                    }]
                }],
                "functions": [{
                    "name": "create_client",
                    "sig": "endpoint: str",
                    "ret": "Client",
                    "async": false
                }]
            }]
        }
        """;

        var raw = System.Text.Json.JsonSerializer.Deserialize(json, RawPythonJsonContext.Default.RawPythonApiIndex);
        Assert.NotNull(raw);
        Assert.Equal("Item", raw.Modules![0].Classes![0].Methods![0].Ret);
        Assert.Equal("Client", raw.Modules[0].Functions![0].Ret);
    }

    [Fact]
    public void PythonDeserialization_PreservesDependencies()
    {
        var json = """
        {
            "package": "test-pkg",
            "modules": [],
            "dependencies": [{
                "package": "azure-core",
                "classes": [{
                    "name": "TokenCredential",
                    "methods": [{"name": "get_token", "sig": "self", "ret": "str"}]
                }]
            }]
        }
        """;

        var raw = System.Text.Json.JsonSerializer.Deserialize(json, RawPythonJsonContext.Default.RawPythonApiIndex);
        Assert.NotNull(raw);
        Assert.NotNull(raw.Dependencies);
        Assert.Single(raw.Dependencies);
        Assert.Equal("azure-core", raw.Dependencies[0].Package);
        Assert.Equal("TokenCredential", raw.Dependencies[0].Classes![0].Name);
    }

    #endregion

    #region Go Formatter

    [Fact]
    public void GoFormatter_RendersConstructor_WithoutFakeReceiver()
    {
        var api = new Go.ApiIndex
        {
            Package = "azblob",
            Packages =
            [
                new Go.PackageApi
                {
                    Name = "azblob",
                    Structs =
                    [
                        new Go.StructApi
                        {
                            Name = "BlobClient",
                            Methods =
                            [
                                // Constructor: empty receiver
                                new Go.FuncApi
                                {
                                    Name = "NewBlobClient",
                                    Sig = "url string, cred TokenCredential",
                                    Receiver = "",
                                    Ret = "*BlobClient"
                                },
                                // Regular method: non-empty receiver
                                new Go.FuncApi
                                {
                                    Name = "Download",
                                    Sig = "ctx context.Context",
                                    Receiver = "*BlobClient",
                                    Ret = "DownloadResponse"
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        var stubs = Go.GoFormatter.Format(api);
        // Constructor should NOT have (receiver) syntax
        Assert.Contains("func NewBlobClient(url string, cred TokenCredential) *BlobClient", stubs);
        Assert.DoesNotContain("func (*BlobClient) NewBlobClient", stubs);
        // Regular method SHOULD have receiver
        Assert.Contains("func (*BlobClient) Download(ctx context.Context) DownloadResponse", stubs);
    }

    [Fact]
    public void GoFormatter_RendersVariables()
    {
        var api = new Go.ApiIndex
        {
            Package = "azblob",
            Packages =
            [
                new Go.PackageApi
                {
                    Name = "azblob",
                    Structs =
                    [
                        new Go.StructApi { Name = "BlobClient" }
                    ],
                    Variables =
                    [
                        new Go.VarApi { Name = "ErrBlobNotFound", Type = "error", Doc = "Returned when blob is not found." }
                    ]
                }
            ]
        };

        var stubs = Go.GoFormatter.Format(api);
        Assert.Contains("var (", stubs);
        Assert.Contains("ErrBlobNotFound error", stubs);
    }

    #endregion

    #region Java Formatter

    [Fact]
    public void JavaFormatter_IncludesInterfaces_InFormatOutput()
    {
        var api = new Java.ApiIndex
        {
            Package = "com.example",
            Packages =
            [
                new Java.PackageInfo
                {
                    Name = "com.example",
                    Classes =
                    [
                        new Java.ClassInfo { Name = "ServiceClient", Methods = [new Java.MethodInfo { Name = "list", Sig = "()", Ret = "List<Item>" }] }
                    ],
                    Interfaces =
                    [
                        new Java.ClassInfo { Name = "Closeable", Methods = [new Java.MethodInfo { Name = "close", Sig = "()", Ret = "void" }] }
                    ]
                }
            ]
        };

        var stubs = Java.JavaFormatter.Format(api);
        Assert.Contains("interface Closeable", stubs);
        Assert.Contains("class ServiceClient", stubs);
    }

    [Fact]
    public void JavaFormatter_LabelsInterfacesCorrectly_InMixedBatch()
    {
        var iface = new Java.ClassInfo { Name = "TokenCredential", Methods = [new Java.MethodInfo { Name = "getToken", Sig = "()", Ret = "Token" }] };
        var cls = new Java.ClassInfo { Name = "DefaultCredential", Methods = [new Java.MethodInfo { Name = "getToken", Sig = "()", Ret = "Token" }] };

        var api = new Java.ApiIndex
        {
            Package = "com.example",
            Packages =
            [
                new Java.PackageInfo
                {
                    Name = "com.example",
                    Classes = [cls],
                    Interfaces = [iface]
                }
            ]
        };

        var stubs = Java.JavaFormatter.Format(api);
        Assert.Contains("interface TokenCredential", stubs);
        Assert.Contains("class DefaultCredential", stubs);
        // Should NOT mislabel class as interface
        Assert.DoesNotContain("interface DefaultCredential", stubs);
    }

    [Fact]
    public void JavaModel_GetAllTypes_IncludesInterfaces()
    {
        var api = new Java.ApiIndex
        {
            Package = "com.example",
            Packages =
            [
                new Java.PackageInfo
                {
                    Name = "com.example",
                    Classes = [new Java.ClassInfo { Name = "Foo" }],
                    Interfaces = [new Java.ClassInfo { Name = "IFoo" }]
                }
            ]
        };

        var allTypes = api.GetAllTypes().ToList();
        Assert.Equal(2, allTypes.Count);
        Assert.Contains(allTypes, t => t.Name == "Foo");
        Assert.Contains(allTypes, t => t.Name == "IFoo");

        // GetAllClasses should still only return classes
        var allClasses = api.GetAllClasses().ToList();
        Assert.Single(allClasses);
        Assert.Equal("Foo", allClasses[0].Name);
    }

    #endregion

    #region TypeScript Formatter

    [Fact]
    public void TypeScriptFormatter_HandlesDuplicateNames_AcrossModules()
    {
        var api = new TypeScript.ApiIndex
        {
            Package = "@azure/test",
            Modules =
            [
                new TypeScript.ModuleInfo
                {
                    Name = "moduleA",
                    Classes = [new TypeScript.ClassInfo { Name = "Client", Methods = [new TypeScript.MethodInfo { Name = "doA", Sig = "()" }] }],
                    Interfaces = [new TypeScript.InterfaceInfo { Name = "Options" }]
                },
                new TypeScript.ModuleInfo
                {
                    Name = "moduleB",
                    Classes = [new TypeScript.ClassInfo { Name = "Client", Methods = [new TypeScript.MethodInfo { Name = "doB", Sig = "()" }] }],
                    Interfaces = [new TypeScript.InterfaceInfo { Name = "Options" }]
                }
            ]
        };

        // This should NOT throw ArgumentException from ToDictionary duplicate key
        var exception = Record.Exception(() => TypeScript.TypeScriptFormatter.Format(api));
        Assert.Null(exception);
    }

    [Fact]
    public void TypeScriptFormatter_FormatWithCoverage_HandlesDuplicateNames()
    {
        var api = new TypeScript.ApiIndex
        {
            Package = "@azure/test",
            Modules =
            [
                new TypeScript.ModuleInfo
                {
                    Name = "moduleA",
                    Classes = [new TypeScript.ClassInfo { Name = "Client", EntryPoint = true, Methods = [new TypeScript.MethodInfo { Name = "doA", Sig = "()" }] }]
                },
                new TypeScript.ModuleInfo
                {
                    Name = "moduleB",
                    Classes = [new TypeScript.ClassInfo { Name = "Client", EntryPoint = true, Methods = [new TypeScript.MethodInfo { Name = "doB", Sig = "()" }] }]
                }
            ]
        };

        var coverage = new UsageIndex
        {
            FileCount = 1,
            CoveredOperations = [new OperationUsage { ClientType = "Client", Operation = "doA", File = "test.ts", Line = 1 }],
            UncoveredOperations = [new UncoveredOperation { ClientType = "Client", Operation = "doB", Signature = "()" }]
        };

        // Should NOT throw
        var exception = Record.Exception(() => TypeScript.TypeScriptFormatter.FormatWithCoverage(api, coverage, int.MaxValue));
        Assert.Null(exception);
    }

    #endregion
}
