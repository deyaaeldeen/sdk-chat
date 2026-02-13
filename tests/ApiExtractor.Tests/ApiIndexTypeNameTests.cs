// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Xunit;

using GoModels = ApiExtractor.Go;
using JavaModels = ApiExtractor.Java;
using TsModels = ApiExtractor.TypeScript;
using PyModels = ApiExtractor.Python;
using DotNetModels = ApiExtractor.DotNet;

namespace ApiExtractor.Tests;

/// <summary>
/// Tests for the IApiIndex.GetAllTypeNames() and GetClientTypeNames() methods
/// added across all 5 language extractors.
/// </summary>
public class ApiIndexTypeNameTests
{
    #region C# (DotNet)

    [Fact]
    public void DotNet_GetAllTypeNames_ReturnsAllTypes()
    {
        var index = new DotNetModels.ApiIndex
        {
            Package = "TestPkg",
            Namespaces =
            [
                new DotNetModels.NamespaceInfo
                {
                    Name = "TestPkg",
                    Types =
                    [
                        new DotNetModels.TypeInfo { Name = "FooClient", Kind = "class", EntryPoint = true, Members = [new DotNetModels.MemberInfo { Name = "Get", Kind = "method", Signature = "void Get()" }] },
                        new DotNetModels.TypeInfo { Name = "BarModel", Kind = "class" },
                        new DotNetModels.TypeInfo { Name = "Color", Kind = "enum" }
                    ]
                }
            ]
        };

        var names = index.GetAllTypeNames().ToList();
        Assert.Equal(3, names.Count);
        Assert.Contains("FooClient", names);
        Assert.Contains("BarModel", names);
        Assert.Contains("Color", names);
    }

    [Fact]
    public void DotNet_GetClientTypeNames_ReturnsOnlyClients()
    {
        var index = new DotNetModels.ApiIndex
        {
            Package = "TestPkg",
            Namespaces =
            [
                new DotNetModels.NamespaceInfo
                {
                    Name = "TestPkg",
                    Types =
                    [
                        new DotNetModels.TypeInfo { Name = "FooClient", Kind = "class", EntryPoint = true, Members = [new DotNetModels.MemberInfo { Name = "Get", Kind = "method", Signature = "void Get()" }] },
                        new DotNetModels.TypeInfo { Name = "BarModel", Kind = "class" }
                    ]
                }
            ]
        };

        var names = index.GetClientTypeNames().ToList();
        Assert.Single(names);
        Assert.Equal("FooClient", names[0]);
    }

    #endregion

    #region Python

    [Fact]
    public void Python_GetAllTypeNames_ReturnsAllClasses()
    {
        var index = new PyModels.ApiIndex("test-pkg",
        [
            new PyModels.ModuleInfo("mod",
            [
                new PyModels.ClassInfo { Name = "MyClient", EntryPoint = true, Methods = [new PyModels.MethodInfo { Name = "op", Signature = "self" }] },
                new PyModels.ClassInfo { Name = "Options" }
            ], null)
        ]);

        var names = index.GetAllTypeNames().ToList();
        Assert.Equal(2, names.Count);
        Assert.Contains("MyClient", names);
        Assert.Contains("Options", names);
    }

    [Fact]
    public void Python_GetClientTypeNames_ReturnsOnlyClients()
    {
        var index = new PyModels.ApiIndex("test-pkg",
        [
            new PyModels.ModuleInfo("mod",
            [
                new PyModels.ClassInfo { Name = "MyClient", EntryPoint = true, Methods = [new PyModels.MethodInfo { Name = "op", Signature = "self" }] },
                new PyModels.ClassInfo { Name = "Options" }
            ], null)
        ]);

        var names = index.GetClientTypeNames().ToList();
        Assert.Single(names);
        Assert.Equal("MyClient", names[0]);
    }

    #endregion

    #region Go

    [Fact]
    public void Go_GetAllTypeNames_IncludesStructsInterfacesAndTypes()
    {
        var index = new GoModels.ApiIndex
        {
            Package = "test",
            Packages =
            [
                new GoModels.PackageApi
                {
                    Name = "pkg",
                    Structs = [new GoModels.StructApi { Name = "Client" }],
                    Interfaces = [new GoModels.IfaceApi { Name = "TokenProvider" }],
                    Types = [new GoModels.TypeApi { Name = "ResponseType", Type = "string" }]
                }
            ]
        };

        var names = index.GetAllTypeNames().ToList();
        Assert.Equal(3, names.Count);
        Assert.Contains("Client", names);
        Assert.Contains("TokenProvider", names);
        Assert.Contains("ResponseType", names);
    }

    [Fact]
    public void Go_GetClientTypeNames_ReturnsOnlyClientStructs()
    {
        var index = new GoModels.ApiIndex
        {
            Package = "test",
            Packages =
            [
                new GoModels.PackageApi
                {
                    Name = "pkg",
                    Structs =
                    [
                        new GoModels.StructApi
                        {
                            Name = "Client",
                            EntryPoint = true,
                            Methods = [new GoModels.FuncApi { Name = "Get", Sig = "ctx context.Context" }]
                        },
                        new GoModels.StructApi { Name = "Options" }
                    ],
                    Interfaces = [new GoModels.IfaceApi { Name = "TokenProvider" }]
                }
            ]
        };

        var names = index.GetClientTypeNames().ToList();
        Assert.Single(names);
        Assert.Equal("Client", names[0]);
    }

    [Fact]
    public void Go_IfaceApi_CollectReferencedTypes_FindsMethodReturnTypes()
    {
        var iface = new GoModels.IfaceApi
        {
            Name = "TokenProvider",
            Methods =
            [
                new GoModels.FuncApi { Name = "GetToken", Sig = "ctx context.Context, opts TokenRequestOptions", Ret = "AccessToken" }
            ]
        };

        var allTypeNames = new HashSet<string>(["TokenProvider", "TokenRequestOptions", "AccessToken", "Client"]);
        var result = new HashSet<string>();
        iface.CollectReferencedTypes(allTypeNames, result);

        Assert.Contains("TokenRequestOptions", result);
        Assert.Contains("AccessToken", result);
        Assert.DoesNotContain("Client", result); // Not referenced
    }

    [Fact]
    public void Go_IfaceApi_CollectReferencedTypes_FindsEmbeddedInterfaces()
    {
        var iface = new GoModels.IfaceApi
        {
            Name = "FullClient",
            Embeds = ["BaseClient", "io.Reader"],
        };

        var allTypeNames = new HashSet<string>(["FullClient", "BaseClient"]);
        var result = new HashSet<string>();
        iface.CollectReferencedTypes(allTypeNames, result);

        Assert.Contains("BaseClient", result);
        Assert.DoesNotContain("io.Reader", result); // Not in allTypeNames
    }

    #endregion

    #region Java

    [Fact]
    public void Java_GetAllTypeNames_IncludesClassesInterfacesEnumsAnnotations()
    {
        var index = new JavaModels.ApiIndex
        {
            Package = "com.test",
            Packages =
            [
                new JavaModels.PackageInfo
                {
                    Name = "com.test",
                    Classes = [new JavaModels.ClassInfo { Name = "TestClient" }],
                    Interfaces = [new JavaModels.ClassInfo { Name = "TokenCredential" }],
                    Enums = [new JavaModels.EnumInfo { Name = "Color" }],
                    Annotations = [new JavaModels.ClassInfo { Name = "ServiceMethod" }]
                }
            ]
        };

        var names = index.GetAllTypeNames().ToList();

        // GetAllTypes() = classes + interfaces + annotations = 3
        // Plus enums = 1
        Assert.Equal(4, names.Count);
        Assert.Contains("TestClient", names);
        Assert.Contains("TokenCredential", names);
        Assert.Contains("Color", names);
        Assert.Contains("ServiceMethod", names);
    }

    [Fact]
    public void Java_GetClientTypeNames_ReturnsOnlyClients()
    {
        var index = new JavaModels.ApiIndex
        {
            Package = "com.test",
            Packages =
            [
                new JavaModels.PackageInfo
                {
                    Name = "com.test",
                    Classes =
                    [
                        new JavaModels.ClassInfo
                        {
                            Name = "TestClient",
                            EntryPoint = true,
                            Methods = [new JavaModels.MethodInfo { Name = "get", Sig = "String id" }]
                        },
                        new JavaModels.ClassInfo { Name = "Options" }
                    ]
                }
            ]
        };

        var names = index.GetClientTypeNames().ToList();
        Assert.Single(names);
        Assert.Equal("TestClient", names[0]);
    }

    #endregion

    #region TypeScript

    [Fact]
    public void TypeScript_GetAllTypeNames_IncludesClassesInterfacesEnumsTypes()
    {
        var index = new TsModels.ApiIndex
        {
            Package = "@test/sdk",
            Modules =
            [
                new TsModels.ModuleInfo
                {
                    Name = "main",
                    Classes = [new TsModels.ClassInfo { Name = "TestClient" }],
                    Interfaces = [new TsModels.InterfaceInfo { Name = "Options" }],
                    Enums = [new TsModels.EnumInfo { Name = "StatusCode" }],
                    Types = [new TsModels.TypeAliasInfo { Name = "InputType", Type = "string | number" }]
                }
            ]
        };

        var names = index.GetAllTypeNames().ToList();
        Assert.Equal(4, names.Count);
        Assert.Contains("TestClient", names);
        Assert.Contains("Options", names);
        Assert.Contains("StatusCode", names);
        Assert.Contains("InputType", names);
    }

    [Fact]
    public void TypeScript_GetClientTypeNames_ReturnsOnlyClients()
    {
        var index = new TsModels.ApiIndex
        {
            Package = "@test/sdk",
            Modules =
            [
                new TsModels.ModuleInfo
                {
                    Name = "main",
                    Classes =
                    [
                        new TsModels.ClassInfo
                        {
                            Name = "TestClient",
                            EntryPoint = true,
                            Methods = [new TsModels.MethodInfo { Name = "get", Sig = "id: string" }]
                        },
                        new TsModels.ClassInfo { Name = "Model" }
                    ]
                }
            ]
        };

        var names = index.GetClientTypeNames().ToList();
        Assert.Single(names);
        Assert.Equal("TestClient", names[0]);
    }

    [Fact]
    public void TypeScript_TypeAliasInfo_CollectReferencedTypes_FindsReferences()
    {
        var typeAlias = new TsModels.TypeAliasInfo
        {
            Name = "UnionType",
            Type = "ModelA | ModelB | string"
        };

        var allTypeNames = new HashSet<string>(["ModelA", "ModelB", "UnionType", "Unrelated"]);
        var result = new HashSet<string>();
        typeAlias.CollectReferencedTypes(allTypeNames, result);

        Assert.Contains("ModelA", result);
        Assert.Contains("ModelB", result);
        Assert.DoesNotContain("Unrelated", result);
        Assert.DoesNotContain("string", result); // not in allTypeNames
    }

    [Fact]
    public void TypeScript_BuildDependencyGraph_IncludesInterfaceAndEnumDeps()
    {
        var index = new TsModels.ApiIndex
        {
            Package = "@test/sdk",
            Modules =
            [
                new TsModels.ModuleInfo
                {
                    Name = "main",
                    Classes =
                    [
                        new TsModels.ClassInfo
                        {
                            Name = "TestClient",
                            Methods =
                            [
                                new TsModels.MethodInfo { Name = "get", Sig = "options: Options", Ret = "Promise<Result>" }
                            ]
                        }
                    ],
                    Interfaces =
                    [
                        new TsModels.InterfaceInfo { Name = "Options" },
                        new TsModels.InterfaceInfo { Name = "Result" }
                    ]
                }
            ]
        };

        var graph = index.BuildDependencyGraph();

        Assert.True(graph.ContainsKey("TestClient"));
        // TestClient's methods reference Options and Result
        Assert.Contains("Options", graph["TestClient"]);
        Assert.Contains("Result", graph["TestClient"]);
    }

    #endregion

    #region Cross-Language Consistency

    [Fact]
    public void AllLanguages_EmptyIndex_ReturnsEmptyTypeNames()
    {
        var dotnet = new DotNetModels.ApiIndex { Package = "test", Namespaces = [] };
        var python = new PyModels.ApiIndex("test", [], null);
        var go = new GoModels.ApiIndex { Package = "test" };
        var java = new JavaModels.ApiIndex { Package = "test" };
        var ts = new TsModels.ApiIndex { Package = "test" };

        Assert.Empty(dotnet.GetAllTypeNames());
        Assert.Empty(dotnet.GetClientTypeNames());
        Assert.Empty(python.GetAllTypeNames());
        Assert.Empty(python.GetClientTypeNames());
        Assert.Empty(go.GetAllTypeNames());
        Assert.Empty(go.GetClientTypeNames());
        Assert.Empty(java.GetAllTypeNames());
        Assert.Empty(java.GetClientTypeNames());
        Assert.Empty(ts.GetAllTypeNames());
        Assert.Empty(ts.GetClientTypeNames());
    }

    #endregion
}
