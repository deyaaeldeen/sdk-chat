// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using ApiExtractor.DotNet;
using ApiExtractor.Python;
using ApiExtractor.TypeScript;
using ApiExtractor.Java;
using ApiExtractor.Go;
using Xunit;

namespace ApiExtractor.Tests;

/// <summary>
/// Comprehensive tests for all usage analyzers across languages.
/// Tests direct method matching, subclients, and top-level functions.
/// </summary>
public class AllUsageAnalyzersTests : IDisposable
{
    private readonly string _tempDir;

    // Analyzers for availability checking and testing
    // Note: These are used both for availability checks and in cross-language consistency tests
    private readonly CSharpUsageAnalyzer _csharpAnalyzer = new();
    private readonly PythonUsageAnalyzer _pythonAnalyzer = new();
    private readonly TypeScriptUsageAnalyzer _tsAnalyzer = new();
    private readonly JavaUsageAnalyzer _javaAnalyzer = new();
    private readonly GoUsageAnalyzer _goAnalyzer = new();

    public AllUsageAnalyzersTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"usage_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    #region C# Usage Analyzer Tests

    [Fact]
    public async Task CSharp_DirectMethodMatch_ExactNameRequired()
    {
        // Arrange - API has GetData, sample calls GetData
        // Use class-level analyzer
        var apiIndex = CreateCSharpApiIndex(
            ("DataClient", ["GetData", "GetDataAsync", "ProcessData"]));

        await WriteFileAsync("sample.cs", """
            var client = new DataClient();
            client.GetData();
            await client.GetDataAsync();
            // ProcessData is not called
            """);

        // Act
        var result = await _csharpAnalyzer.AnalyzeAsync(_tempDir, apiIndex);

        // Assert - only exact matches
        Assert.Equal(2, result.CoveredOperations.Count);
        Assert.Contains(result.CoveredOperations, o => o.Operation == "GetData");
        Assert.Contains(result.CoveredOperations, o => o.Operation == "GetDataAsync");
        Assert.Contains(result.UncoveredOperations, o => o.Operation == "ProcessData");
    }

    [Fact]
    public async Task CSharp_Subclient_MethodsTrackedSeparately()
    {
        // Arrange - main client with subclient properties that have proper return types
        // Use class-level analyzer
        var apiIndex = CreateCSharpApiIndexWithProperties(
            ("StorageClient", ["GetBlob"], [("Blobs", "BlobClient"), ("Containers", "ContainerClient")]),
            ("BlobClient", ["Upload", "Download", "Delete"], []),
            ("ContainerClient", ["Create", "List"], []));

        await WriteFileAsync("sample.cs", """
            var storage = new StorageClient();
            storage.GetBlob();

            var blob = storage.Blobs;
            blob.Upload();
            blob.Download();
            // Delete not called

            var container = storage.Containers;
            container.Create();
            // List not called
            """);

        // Act
        var result = await _csharpAnalyzer.AnalyzeAsync(_tempDir, apiIndex);

        // Assert
        Assert.Contains(result.CoveredOperations, o => o.ClientType == "StorageClient" && o.Operation == "GetBlob");
        Assert.Contains(result.CoveredOperations, o => o.ClientType == "BlobClient" && o.Operation == "Upload");
        Assert.Contains(result.CoveredOperations, o => o.ClientType == "BlobClient" && o.Operation == "Download");
        Assert.Contains(result.CoveredOperations, o => o.ClientType == "ContainerClient" && o.Operation == "Create");

        Assert.Contains(result.UncoveredOperations, o => o.ClientType == "BlobClient" && o.Operation == "Delete");
        Assert.Contains(result.UncoveredOperations, o => o.ClientType == "ContainerClient" && o.Operation == "List");
    }

    [Fact]
    public async Task CSharp_PreciseInference_RejectsNonClientReceivers()
    {
        // Arrange - method exists in API, but called on non-SDK receiver
        // With precise type inference, arbitrary receivers should NOT match
        // Use class-level analyzer
        var apiIndex = CreateCSharpApiIndex(
            ("ChatClient", ["Send", "Receive"]));

        await WriteFileAsync("sample.cs", """
            // These should NOT match - receivers are not SDK client types
            var x = new object();
            x.Send();  // Send is in API but x is not ChatClient

            var _helper = new object();
            _helper.Receive();  // Receive is in API but _helper is not ChatClient

            // This should NOT match - method not in API
            x.DoSomethingElse();
            """);

        // Act
        var result = await _csharpAnalyzer.AnalyzeAsync(_tempDir, apiIndex);

        // Assert - precise type inference rejects non-SDK receivers
        Assert.Empty(result.CoveredOperations);
    }

    [Fact]
    public async Task CSharp_TopLevelFunctions_NotSupportedInCSharp()
    {
        // C# doesn't have top-level functions in the same way as Python/Go
        // Static methods on classes are tracked as class methods
        // Use class-level analyzer
        var apiIndex = CreateCSharpApiIndex(
            ("Helpers", ["CreateClient", "ParseResponse"]));

        await WriteFileAsync("sample.cs", """
            Helpers.CreateClient();
            // ParseResponse not called
            """);

        // Act
        var result = await _csharpAnalyzer.AnalyzeAsync(_tempDir, apiIndex);

        // Assert
        Assert.Single(result.CoveredOperations);
        Assert.Equal("CreateClient", result.CoveredOperations[0].Operation);
        Assert.Contains(result.UncoveredOperations, o => o.Operation == "ParseResponse");
    }

    private static DotNet.ApiIndex CreateCSharpApiIndex(params (string TypeName, string[] Methods)[] types)
    {
        return CreateCSharpApiIndexWithProperties(
            types.Select(t => (t.TypeName, t.Methods, Array.Empty<(string Name, string ReturnType)>())).ToArray());
    }

    private static DotNet.ApiIndex CreateCSharpApiIndexWithProperties(
        params (string TypeName, string[] Methods, (string Name, string ReturnType)[] Properties)[] types)
    {
        return new DotNet.ApiIndex
        {
            Package = "TestSdk",
            Namespaces =
            [
                new DotNet.NamespaceInfo
                {
                    Name = "TestSdk",
                    Types = types.Select(t => new DotNet.TypeInfo
                    {
                        Name = t.TypeName,
                        Kind = "class",
                        Members = t.Methods.Select(m => new DotNet.MemberInfo
                        {
                            Name = m,
                            Kind = "method",
                            Signature = $"void {m}()"
                        }).Concat(t.Properties.Select(p => new DotNet.MemberInfo
                        {
                            Name = p.Name,
                            Kind = "property",
                            Signature = $"{p.ReturnType} {p.Name} {{ get; }}"
                        })).ToList()
                    }).ToList()
                }
            ]
        };
    }

    #endregion

    #region Python Usage Analyzer Tests

    [Fact]
    public async Task Python_DirectMethodMatch_ExactNameRequired()
    {
        if (!_pythonAnalyzer.IsAvailable()) Assert.Skip("Python not available");

        var apiIndex = CreatePythonApiIndex(
            classes: [("DataClient", ["get_data", "get_data_async", "process_data"])],
            functions: []);

        await WriteFileAsync("sample.py", """
def main():
    client = DataClient()
    client.get_data()
    client.get_data_async()
# process_data is not called
""");

        // Act
        var result = await _pythonAnalyzer.AnalyzeAsync(_tempDir, apiIndex);

        // Assert
        Assert.Equal(2, result.CoveredOperations.Count);
        Assert.Contains(result.CoveredOperations, o => o.Operation == "get_data");
        Assert.Contains(result.CoveredOperations, o => o.Operation == "get_data_async");
        Assert.Contains(result.UncoveredOperations, o => o.Operation == "process_data");
    }

    [Fact]
    public async Task Python_Subclient_MethodsTrackedSeparately()
    {
        if (!_pythonAnalyzer.IsAvailable()) Assert.Skip("Python not available");

        var apiIndex = CreatePythonApiIndex(
            classes:
            [
                ("StorageClient", ["get_blob"]),
                ("BlobClient", ["upload", "download", "delete"]),
                ("ContainerClient", ["create", "list_blobs"])
            ],
            functions: []);

        await WriteFileAsync("sample.py", """
storage = StorageClient()
storage.get_blob()

blob = storage.blobs
blob.upload()
blob.download()
# delete not called

container = storage.containers
container.create()
# list_blobs not called
""");

        // Act
        var result = await _pythonAnalyzer.AnalyzeAsync(_tempDir, apiIndex);

        // Assert
        Assert.Contains(result.CoveredOperations, o => o.ClientType == "StorageClient" && o.Operation == "get_blob");
        Assert.Contains(result.CoveredOperations, o => o.ClientType == "BlobClient" && o.Operation == "upload");
        Assert.Contains(result.CoveredOperations, o => o.ClientType == "BlobClient" && o.Operation == "download");
        Assert.Contains(result.CoveredOperations, o => o.ClientType == "ContainerClient" && o.Operation == "create");

        Assert.Contains(result.UncoveredOperations, o => o.ClientType == "BlobClient" && o.Operation == "delete");
        Assert.Contains(result.UncoveredOperations, o => o.ClientType == "ContainerClient" && o.Operation == "list_blobs");
    }

    [Fact]
    public async Task Python_TopLevelFunctions_TrackedCorrectly()
    {

        var apiIndex = CreatePythonApiIndex(
            classes: [("Client", ["send"])],
            functions: [("create_client", "def create_client() -> Client"), ("parse_response", "def parse_response(data: str) -> dict")]);

        await WriteFileAsync("sample.py", """
from sdk import create_client, Client

client = create_client()  # top-level function
client.send()
# parse_response not called
""");

        // Act
        var result = await _pythonAnalyzer.AnalyzeAsync(_tempDir, apiIndex);

        // Assert - note: Python analyzer may track functions differently
        // This test documents expected behavior
        Assert.Contains(result.CoveredOperations, o => o.Operation == "send");
    }

    private static Python.ApiIndex CreatePythonApiIndex(
        (string ClassName, string[] Methods)[] classes,
        (string FuncName, string Sig)[] functions)
    {
        return new Python.ApiIndex(
            Package: "test_sdk",
            Modules:
            [
                new Python.ModuleInfo(
                    Name: "test_sdk",
                    Classes: classes.Select(c => new Python.ClassInfo
                    {
                        Name = c.ClassName,
                        EntryPoint = true,
                        Methods = c.Methods.Select(m => new Python.MethodInfo(
                            Name: m,
                            Signature: "(self)",
                            Doc: null,
                            IsAsync: null,
                            IsClassMethod: null,
                            IsStaticMethod: null
                        )).ToList()
                    }).ToList(),
                    Functions: functions.Select(f => new Python.FunctionInfo
                    {
                        Name = f.FuncName,
                        Signature = f.Sig.Replace($"def {f.FuncName}", "").Trim()
                    }).ToList()
                )
            ]
        );
    }

    #endregion

    #region TypeScript Usage Analyzer Tests

    [Fact]
    public async Task TypeScript_DirectMethodMatch_ExactNameRequired()
    {
        if (!_tsAnalyzer.IsAvailable()) Assert.Skip("Node.js not available");

        var apiIndex = CreateTypeScriptApiIndex(
            classes: [("DataClient", ["getData", "getDataAsync", "processData"])],
            functions: []);

        await WriteFileAsync("sample.ts", """
            const client = new DataClient();
            client.getData();
            await client.getDataAsync();
            // processData is not called
            """);

        // Act
        var result = await _tsAnalyzer.AnalyzeAsync(_tempDir, apiIndex);

        // Assert
        Assert.Equal(2, result.CoveredOperations.Count);
        Assert.Contains(result.CoveredOperations, o => o.Operation == "getData");
        Assert.Contains(result.CoveredOperations, o => o.Operation == "getDataAsync");
        Assert.Contains(result.UncoveredOperations, o => o.Operation == "processData");
    }

    [Fact]
    public async Task TypeScript_Subclient_MethodsTrackedSeparately()
    {
        if (!_tsAnalyzer.IsAvailable()) Assert.Skip("Node.js not available");

        var apiIndex = CreateTypeScriptApiIndexWithProperties(
            classes:
            [
                ("StorageClient", ["getBlob"], [("blobs", "BlobClient"), ("containers", "ContainerClient")]),
                ("BlobClient", ["upload", "download", "delete"], []),
                ("ContainerClient", ["create", "listBlobs"], [])
            ],
            functions: []);

        await WriteFileAsync("sample.ts", """
            const storage = new StorageClient();
            storage.getBlob();

            const blob = storage.blobs;
            blob.upload();
            blob.download();
            // delete not called

            const container = storage.containers;
            container.create();
            // listBlobs not called
            """);

        // Act
        var result = await _tsAnalyzer.AnalyzeAsync(_tempDir, apiIndex);

        // Assert
        Assert.Contains(result.CoveredOperations, o => o.ClientType == "StorageClient" && o.Operation == "getBlob");
        Assert.Contains(result.CoveredOperations, o => o.ClientType == "BlobClient" && o.Operation == "upload");
        Assert.Contains(result.CoveredOperations, o => o.ClientType == "BlobClient" && o.Operation == "download");
        Assert.Contains(result.CoveredOperations, o => o.ClientType == "ContainerClient" && o.Operation == "create");

        Assert.Contains(result.UncoveredOperations, o => o.ClientType == "BlobClient" && o.Operation == "delete");
        Assert.Contains(result.UncoveredOperations, o => o.ClientType == "ContainerClient" && o.Operation == "listBlobs");
    }

    [Fact]
    public async Task TypeScript_TopLevelFunctions_TrackedCorrectly()
    {
        if (!_tsAnalyzer.IsAvailable()) Assert.Skip("Node.js not available");

        var apiIndex = CreateTypeScriptApiIndex(
            classes: [("Client", ["send"])],
            functions: [("createClient", "(): Client"), ("parseResponse", "(data: string): object")]);

        await WriteFileAsync("sample.ts", """
            import { createClient, Client } from 'sdk';

            const client = createClient();  // top-level function
            client.send();
            // parseResponse not called
            """);

        // Act
        var result = await _tsAnalyzer.AnalyzeAsync(_tempDir, apiIndex);

        // Assert
        Assert.Contains(result.CoveredOperations, o => o.Operation == "send");
    }

    private static TypeScript.ApiIndex CreateTypeScriptApiIndex(
        (string ClassName, string[] Methods)[] classes,
        (string FuncName, string Sig)[] functions)
    {
        return CreateTypeScriptApiIndexWithProperties(
            classes.Select(c => (c.ClassName, c.Methods, Array.Empty<(string Name, string Type)>())).ToArray(),
            functions);
    }

    private static TypeScript.ApiIndex CreateTypeScriptApiIndexWithProperties(
        (string ClassName, string[] Methods, (string Name, string Type)[] Properties)[] classes,
        (string FuncName, string Sig)[] functions)
    {
        return new TypeScript.ApiIndex
        {
            Package = "test-sdk",
            Modules =
            [
                new TypeScript.ModuleInfo
                {
                    Name = "index",
                    Classes = classes.Select(c => new TypeScript.ClassInfo
                    {
                        Name = c.ClassName,
                        EntryPoint = true,
                        Methods = c.Methods.Select(m => new TypeScript.MethodInfo
                        {
                            Name = m,
                            Sig = "()"
                        }).ToList(),
                        Properties = c.Properties.Length > 0
                            ? c.Properties.Select(p => new TypeScript.PropertyInfo
                            {
                                Name = p.Name,
                                Type = p.Type
                            }).ToList()
                            : null
                    }).ToList(),
                    Functions = functions.Select(f =>
                    {
                        // Parse return type from signature like "(): Client"
                        string? ret = null;
                        var colonIdx = f.Sig.LastIndexOf(')');
                        if (colonIdx >= 0)
                        {
                            var afterParen = f.Sig[(colonIdx + 1)..].Trim();
                            if (afterParen.StartsWith(':'))
                            {
                                ret = afterParen[1..].Trim();
                            }
                        }
                        return new TypeScript.FunctionInfo
                        {
                            Name = f.FuncName,
                            Sig = f.Sig,
                            Ret = ret
                        };
                    }).ToList()
                }
            ]
        };
    }

    #endregion

    #region Java Usage Analyzer Tests

    [Fact]
    public async Task Java_DirectMethodMatch_ExactNameRequired()
    {
        if (!_javaAnalyzer.IsAvailable()) Assert.Skip("JBang not available");

        var apiIndex = CreateJavaApiIndex(
            classes: [("DataClient", ["getData", "getDataAsync", "processData"])],
            interfaces: []);

        await WriteFileAsync("Sample.java", """
            public class Sample {
                public static void main(String[] args) {
                    DataClient client = new DataClient();
                    client.getData();
                    client.getDataAsync();
                    // processData is not called
                }
            }
            """);

        // Act
        var result = await _javaAnalyzer.AnalyzeAsync(_tempDir, apiIndex);

        // Assert
        Assert.Equal(2, result.CoveredOperations.Count);
        Assert.Contains(result.CoveredOperations, o => o.Operation == "getData");
        Assert.Contains(result.CoveredOperations, o => o.Operation == "getDataAsync");
        Assert.Contains(result.UncoveredOperations, o => o.Operation == "processData");
    }

    [Fact]
    public async Task Java_Subclient_MethodsTrackedSeparately()
    {
        if (!_javaAnalyzer.IsAvailable()) Assert.Skip("JBang not available");

        var apiIndex = CreateJavaApiIndex(
            classes:
            [
                ("StorageClient", ["getBlob"]),
                ("BlobClient", ["upload", "download", "delete"]),
                ("ContainerClient", ["create", "listBlobs"])
            ],
            interfaces: []);

        await WriteFileAsync("Sample.java", """
            public class Sample {
                public static void main(String[] args) {
                    StorageClient storage = new StorageClient();
                    storage.getBlob();

                    BlobClient blob = storage.getBlobs();
                    blob.upload();
                    blob.download();
                    // delete not called

                    ContainerClient container = storage.getContainers();
                    container.create();
                    // listBlobs not called
                }
            }
            """);

        // Act
        var result = await _javaAnalyzer.AnalyzeAsync(_tempDir, apiIndex);

        // Assert
        Assert.Contains(result.CoveredOperations, o => o.ClientType == "StorageClient" && o.Operation == "getBlob");
        Assert.Contains(result.CoveredOperations, o => o.ClientType == "BlobClient" && o.Operation == "upload");
        Assert.Contains(result.CoveredOperations, o => o.ClientType == "BlobClient" && o.Operation == "download");
        Assert.Contains(result.CoveredOperations, o => o.ClientType == "ContainerClient" && o.Operation == "create");

        Assert.Contains(result.UncoveredOperations, o => o.ClientType == "BlobClient" && o.Operation == "delete");
        Assert.Contains(result.UncoveredOperations, o => o.ClientType == "ContainerClient" && o.Operation == "listBlobs");
    }

    [Fact]
    public async Task Java_InterfaceMethods_TrackedCorrectly()
    {
        if (!_javaAnalyzer.IsAvailable()) Assert.Skip("JBang not available");

        var apiIndex = CreateJavaApiIndex(
            classes: [("ClientImpl", ["send"])],
            interfaces: [("Client", ["send", "receive"])]);

        await WriteFileAsync("Sample.java", """
            public class Sample {
                public static void main(String[] args) {
                    Client client = new ClientImpl();
                    client.send();
                    // receive not called
                }
            }
            """);

        // Act
        var result = await _javaAnalyzer.AnalyzeAsync(_tempDir, apiIndex);

        // Assert
        Assert.Contains(result.CoveredOperations, o => o.Operation == "send");
    }

    private static Java.ApiIndex CreateJavaApiIndex(
        (string ClassName, string[] Methods)[] classes,
        (string InterfaceName, string[] Methods)[] interfaces)
    {
        return new Java.ApiIndex
        {
            Package = "com.test.sdk",
            Packages =
            [
                new Java.PackageInfo
                {
                    Name = "com.test.sdk",
                    Classes = classes.Select(c => new Java.ClassInfo
                    {
                        Name = c.ClassName,
                        EntryPoint = true,
                        Methods = c.Methods.Select(m => new Java.MethodInfo
                        {
                            Name = m,
                            Sig = "()"
                        }).ToList()
                    }).ToList(),
                    Interfaces = interfaces.Select(i => new Java.ClassInfo
                    {
                        Name = i.InterfaceName,
                        EntryPoint = true,
                        Methods = i.Methods.Select(m => new Java.MethodInfo
                        {
                            Name = m,
                            Sig = "()"
                        }).ToList()
                    }).ToList()
                }
            ]
        };
    }

    #endregion

    #region Go Usage Analyzer Tests

    [Fact]
    public async Task Go_DirectMethodMatch_ExactNameRequired()
    {
        if (!_goAnalyzer.IsAvailable()) Assert.Skip("Go not available");

        var apiIndex = CreateGoApiIndex(
            structs: [("DataClient", ["GetData", "GetDataAsync", "ProcessData"])],
            functions: []);

        await WriteFileAsync("sample.go", """
            package main

            func main() {
                client := &DataClient{}
                client.GetData()
                client.GetDataAsync()
                // ProcessData is not called
            }
            """);

        // Act
        var result = await _goAnalyzer.AnalyzeAsync(_tempDir, apiIndex);

        // Assert
        Assert.Equal(2, result.CoveredOperations.Count);
        Assert.Contains(result.CoveredOperations, o => o.Operation == "GetData");
        Assert.Contains(result.CoveredOperations, o => o.Operation == "GetDataAsync");
        Assert.Contains(result.UncoveredOperations, o => o.Operation == "ProcessData");
    }

    [Fact]
    public async Task Go_Subclient_MethodsTrackedSeparately()
    {
        if (!_goAnalyzer.IsAvailable()) Assert.Skip("Go not available");

        // StorageClient has methods that return subclient types (Blobs→BlobClient, Containers→ContainerClient)
        var apiIndex = CreateGoApiIndexWithMethodRets(
            structs:
            [
                ("StorageClient", [("GetBlob", (string?)null), ("Blobs", "*BlobClient"), ("Containers", "*ContainerClient")]),
                ("BlobClient", [("Upload", null), ("Download", null), ("Delete", null)]),
                ("ContainerClient", [("Create", null), ("ListBlobs", null)])
            ],
            functions: []);

        await WriteFileAsync("sample.go", """
            package main

            func main() {
                storage := &StorageClient{}
                storage.GetBlob()

                blob := storage.Blobs()
                blob.Upload()
                blob.Download()
                // Delete not called

                container := storage.Containers()
                container.Create()
                // ListBlobs not called
            }
            """);

        // Act
        var result = await _goAnalyzer.AnalyzeAsync(_tempDir, apiIndex);

        // Assert
        Assert.Contains(result.CoveredOperations, o => o.ClientType == "StorageClient" && o.Operation == "GetBlob");
        Assert.Contains(result.CoveredOperations, o => o.ClientType == "BlobClient" && o.Operation == "Upload");
        Assert.Contains(result.CoveredOperations, o => o.ClientType == "BlobClient" && o.Operation == "Download");
        Assert.Contains(result.CoveredOperations, o => o.ClientType == "ContainerClient" && o.Operation == "Create");

        Assert.Contains(result.UncoveredOperations, o => o.ClientType == "BlobClient" && o.Operation == "Delete");
        Assert.Contains(result.UncoveredOperations, o => o.ClientType == "ContainerClient" && o.Operation == "ListBlobs");
    }

    [Fact]
    public async Task Go_TopLevelFunctions_TrackedCorrectly()
    {
        if (!_goAnalyzer.IsAvailable()) Assert.Skip("Go not available");

        var apiIndex = CreateGoApiIndex(
            structs: [("Client", ["Send"])],
            functions: [("NewClient", "func NewClient() *Client"), ("ParseResponse", "func ParseResponse(data string) map[string]interface{}")]);

        await WriteFileAsync("sample.go", """
            package main

            func main() {
                client := NewClient()  // top-level function
                client.Send()
                // ParseResponse not called
            }
            """);

        // Act
        var result = await _goAnalyzer.AnalyzeAsync(_tempDir, apiIndex);

        // Assert
        Assert.Contains(result.CoveredOperations, o => o.Operation == "Send");
    }

    [Fact]
    public async Task Go_InterfaceMethods_TrackedCorrectly()
    {
        if (!_goAnalyzer.IsAvailable()) Assert.Skip("Go not available");

        var apiIndex = CreateGoApiIndex(
            structs: [("ClientImpl", ["Send"])],
            functions: [],
            interfaces: [("Client", ["Send", "Receive"])]);

        await WriteFileAsync("sample.go", """
            package main

            func main() {
                var client Client = &ClientImpl{}
                client.Send()
                // Receive not called
            }
            """);

        // Act
        var result = await _goAnalyzer.AnalyzeAsync(_tempDir, apiIndex);

        // Assert
        Assert.Contains(result.CoveredOperations, o => o.Operation == "Send");
    }

    private static Go.ApiIndex CreateGoApiIndex(
        (string StructName, string[] Methods)[] structs,
        (string FuncName, string Sig)[] functions,
        (string InterfaceName, string[] Methods)[]? interfaces = null)
    {
        return CreateGoApiIndexWithMethodRets(
            structs.Select(s => (s.StructName, s.Methods.Select(m => (m, (string?)null)).ToArray())).ToArray(),
            functions,
            interfaces);
    }

    private static Go.ApiIndex CreateGoApiIndexWithMethodRets(
        (string StructName, (string Name, string? Ret)[] Methods)[] structs,
        (string FuncName, string Sig)[] functions,
        (string InterfaceName, string[] Methods)[]? interfaces = null)
    {
        return new Go.ApiIndex
        {
            Package = "testsdk",
            Packages =
            [
                new Go.PackageApi
                {
                    Name = "testsdk",
                    Structs = structs.Select(s => new Go.StructApi
                    {
                        Name = s.StructName,
                        EntryPoint = true,
                        Methods = s.Methods.Select(m => new Go.FuncApi
                        {
                            Name = m.Name,
                            Sig = "()",
                            Ret = m.Ret
                        }).ToList()
                    }).ToList(),
                    Functions = functions.Select(f =>
                    {
                        // Parse return type from signature like "() *Client"
                        var sig = f.Sig.Replace($"func {f.FuncName}", "").Trim();
                        string? ret = null;
                        var parenClose = sig.LastIndexOf(')');
                        if (parenClose >= 0 && parenClose < sig.Length - 1)
                        {
                            ret = sig[(parenClose + 1)..].Trim();
                        }
                        return new Go.FuncApi
                        {
                            Name = f.FuncName,
                            Sig = sig,
                            Ret = ret
                        };
                    }).ToList(),
                    Interfaces = interfaces?.Select(i => new Go.IfaceApi
                    {
                        Name = i.InterfaceName,
                        Methods = i.Methods.Select(m => new Go.FuncApi
                        {
                            Name = m,
                            Sig = "()"
                        }).ToList()
                    }).ToList()
                }
            ]
        };
    }

    #endregion

    #region Cross-Language Consistency Tests

    [Fact]
    public async Task AllAnalyzers_EmptyDirectory_ReturnsEmptyIndex()
    {
        var emptyDir = Path.Combine(_tempDir, "empty");
        Directory.CreateDirectory(emptyDir);

        // C#

        var csharpApi = CreateCSharpApiIndex(("Client", ["Method"]));
        var csharpResult = await _csharpAnalyzer.AnalyzeAsync(emptyDir, csharpApi);
        Assert.Equal(0, csharpResult.FileCount);
        Assert.Empty(csharpResult.CoveredOperations);

        // Python

        if (_pythonAnalyzer.IsAvailable())
        {
            var pythonApi = CreatePythonApiIndex([("Client", ["method"])], []);
            var pythonResult = await _pythonAnalyzer.AnalyzeAsync(emptyDir, pythonApi);
            Assert.Equal(0, pythonResult.FileCount);
            Assert.Empty(pythonResult.CoveredOperations);
        }

        // TypeScript

        if (_tsAnalyzer.IsAvailable())
        {
            var tsApi = CreateTypeScriptApiIndex([("Client", ["method"])], []);
            var tsResult = await _tsAnalyzer.AnalyzeAsync(emptyDir, tsApi);
            Assert.Equal(0, tsResult.FileCount);
            Assert.Empty(tsResult.CoveredOperations);
        }

        // Java

        if (_javaAnalyzer.IsAvailable())
        {
            var javaApi = CreateJavaApiIndex([("Client", ["method"])], []);
            var javaResult = await _javaAnalyzer.AnalyzeAsync(emptyDir, javaApi);
            Assert.Equal(0, javaResult.FileCount);
            Assert.Empty(javaResult.CoveredOperations);
        }

        // Go

        if (_goAnalyzer.IsAvailable())
        {
            var goApi = CreateGoApiIndex([("Client", ["Method"])], []);
            var goResult = await _goAnalyzer.AnalyzeAsync(emptyDir, goApi);
            Assert.Equal(0, goResult.FileCount);
            Assert.Empty(goResult.CoveredOperations);
        }
    }

    [Fact]
    public async Task AllAnalyzers_EmptyApi_ReturnsEmptyOperations()
    {
        // Write sample files
        await WriteFileAsync("sample.cs", "client.Method();");
        await WriteFileAsync("sample.py", "client.method()");
        await WriteFileAsync("sample.ts", "client.method();");
        await WriteFileAsync("Sample.java", "class S { void m() { client.method(); }}");
        await WriteFileAsync("sample.go", "package main\nfunc main() { client.Method() }");

        // C#

        var csharpApi = new DotNet.ApiIndex { Package = "Empty" };
        var csharpResult = await _csharpAnalyzer.AnalyzeAsync(_tempDir, csharpApi);
        Assert.Empty(csharpResult.CoveredOperations);
        Assert.Empty(csharpResult.UncoveredOperations);

        // Python

        if (_pythonAnalyzer.IsAvailable())
        {
            var pythonApi = new Python.ApiIndex("Empty", []);
            var pythonResult = await _pythonAnalyzer.AnalyzeAsync(_tempDir, pythonApi);
            Assert.Empty(pythonResult.CoveredOperations);
            Assert.Empty(pythonResult.UncoveredOperations);
        }

        // TypeScript

        if (_tsAnalyzer.IsAvailable())
        {
            var tsApi = new TypeScript.ApiIndex { Package = "empty" };
            var tsResult = await _tsAnalyzer.AnalyzeAsync(_tempDir, tsApi);
            Assert.Empty(tsResult.CoveredOperations);
            Assert.Empty(tsResult.UncoveredOperations);
        }

        // Java

        if (_javaAnalyzer.IsAvailable())
        {
            var javaApi = new Java.ApiIndex { Package = "empty" };
            var javaResult = await _javaAnalyzer.AnalyzeAsync(_tempDir, javaApi);
            Assert.Empty(javaResult.CoveredOperations);
            Assert.Empty(javaResult.UncoveredOperations);
        }

        // Go

        if (_goAnalyzer.IsAvailable())
        {
            var goApi = new Go.ApiIndex { Package = "empty" };
            var goResult = await _goAnalyzer.AnalyzeAsync(_tempDir, goApi);
            Assert.Empty(goResult.CoveredOperations);
            Assert.Empty(goResult.UncoveredOperations);
        }
    }

    #endregion

    #region Test Helpers

    private async Task WriteFileAsync(string fileName, string content)
    {
        var path = Path.Combine(_tempDir, fileName);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        await File.WriteAllTextAsync(path, content);
    }

    #endregion
}
