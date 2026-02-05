// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using ApiExtractor.DotNet;
using Xunit;

namespace ApiExtractor.Tests;

/// <summary>
/// Tests for ApiExtractor model immutability - verifies IReadOnlyList usage.
/// </summary>
public class ApiModelImmutabilityTests
{
    [Fact]
    public void DotNetApiIndex_Namespaces_IsIReadOnlyList()
    {
        // Arrange
        var index = new ApiIndex
        {
            Package = "Test.Package",
            Namespaces = [new NamespaceInfo { Name = "Test" }]
        };

        // Assert - verify it's IReadOnlyList (cannot cast to List and mutate)
        Assert.IsAssignableFrom<IReadOnlyList<NamespaceInfo>>(index.Namespaces);
    }

    [Fact]
    public void DotNetNamespaceInfo_Types_IsIReadOnlyList()
    {
        var ns = new NamespaceInfo
        {
            Name = "Test.Namespace",
            Types = [new TypeInfo { Name = "TestClass", Kind = "class" }]
        };

        Assert.IsAssignableFrom<IReadOnlyList<TypeInfo>>(ns.Types);
    }

    [Fact]
    public void DotNetTypeInfo_Members_IsIReadOnlyList()
    {
        var type = new TypeInfo
        {
            Name = "TestClass",
            Kind = "class",
            Members = [new MemberInfo { Name = "Method1", Kind = "method", Signature = "void Method1()" }],
            Interfaces = ["IDisposable"],
            Values = ["Value1", "Value2"]
        };

        Assert.IsAssignableFrom<IReadOnlyList<MemberInfo>>(type.Members);
        Assert.IsAssignableFrom<IReadOnlyList<string>>(type.Interfaces);
        Assert.IsAssignableFrom<IReadOnlyList<string>>(type.Values);
    }

    [Fact]
    public void DotNetApiIndex_CanBeCreatedWithArrayInitializer()
    {
        // Verify collection expressions work with IReadOnlyList
        var index = new ApiIndex
        {
            Package = "Test",
            Namespaces =
            [
                new NamespaceInfo
                {
                    Name = "NS1",
                    Types =
                    [
                        new TypeInfo
                        {
                            Name = "Class1",
                            Kind = "class",
                            Members =
                            [
                                new MemberInfo { Name = "M1", Kind = "method", Signature = "void M1()" }
                            ]
                        }
                    ]
                }
            ]
        };

        Assert.Single(index.Namespaces);
        Assert.Single(index.Namespaces[0].Types);
        Assert.Single(index.Namespaces[0].Types[0].Members!);
    }

    [Fact]
    public void DotNetApiIndex_EmptyCollections_Work()
    {
        var index = new ApiIndex
        {
            Package = "Empty",
            Namespaces = []
        };

        Assert.Empty(index.Namespaces);
    }

    [Fact]
    public void DotNetTypeInfo_GetAllTypes_WorksWithIReadOnlyList()
    {
        var index = new ApiIndex
        {
            Package = "Test",
            Namespaces =
            [
                new NamespaceInfo
                {
                    Name = "NS1",
                    Types = [new TypeInfo { Name = "T1", Kind = "class" }]
                },
                new NamespaceInfo
                {
                    Name = "NS2",
                    Types = [new TypeInfo { Name = "T2", Kind = "interface" }]
                }
            ]
        };

        var allTypes = index.GetAllTypes().ToList();

        Assert.Equal(2, allTypes.Count);
        Assert.Contains(allTypes, t => t.Name == "T1");
        Assert.Contains(allTypes, t => t.Name == "T2");
    }

    [Fact]
    public void DotNetTypeInfo_GetClientTypes_WorksWithIReadOnlyList()
    {
        var index = new ApiIndex
        {
            Package = "Test",
            Namespaces =
            [
                new NamespaceInfo
                {
                    Name = "NS1",
                    Types =
                    [
                        new TypeInfo
                        {
                            Name = "TestClient",
                            Kind = "class",
                            EntryPoint = true,
                            Members = [new MemberInfo { Name = "DoSomething", Kind = "method", Signature = "Task DoSomething()" }]
                        },
                        new TypeInfo { Name = "TestModel", Kind = "class" }
                    ]
                }
            ]
        };

        var clients = index.GetClientTypes().ToList();

        Assert.Single(clients);
        Assert.Equal("TestClient", clients[0].Name);
    }
}
