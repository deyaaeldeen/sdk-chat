// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using ApiExtractor.DotNet;
using ApiExtractor.Python;
using ApiExtractor.Java;
using ApiExtractor.Go;
using ApiExtractor.TypeScript;
using Xunit;

namespace ApiExtractor.Tests;

/// <summary>
/// Tests for the smart truncation feature across all API extractors.
/// Ensures that priority-based truncation preserves semantic completeness.
/// </summary>
public class SmartTruncationTests
{
    #region DotNet Smart Truncation Tests

    [Fact]
    public void DotNet_TypeInfo_IsClientType_DetectsClientClasses()
    {
        var clientType = new DotNet.TypeInfo
        {
            Name = "ChatClient",
            Kind = "class",
            Members = [new MemberInfo { Name = "SendAsync", Kind = "method", Signature = "() -> Task" }]
        };
        
        var modelType = new DotNet.TypeInfo
        {
            Name = "ChatMessage",
            Kind = "class",
            Members = [new MemberInfo { Name = "Content", Kind = "property", Signature = "string" }]
        };
        
        Assert.True(clientType.IsClientType);
        Assert.False(modelType.IsClientType);
    }

    [Fact]
    public void DotNet_TypeInfo_IsModelType_DetectsModelClasses()
    {
        var modelType = new DotNet.TypeInfo
        {
            Name = "ChatMessage",
            Kind = "class",
            Members = [new MemberInfo { Name = "Content", Kind = "property", Signature = "string" }]
        };
        
        var clientType = new DotNet.TypeInfo
        {
            Name = "ChatClient",
            Kind = "class",
            Members = [new MemberInfo { Name = "SendAsync", Kind = "method", Signature = "() -> Task" }]
        };
        
        Assert.True(modelType.IsModelType);
        Assert.False(clientType.IsModelType);
    }

    [Fact]
    public void DotNet_TypeInfo_TruncationPriority_ClientsHaveHighestPriority()
    {
        var clientType = new DotNet.TypeInfo
        {
            Name = "ChatClient",
            Kind = "class",
            Members = [new MemberInfo { Name = "SendAsync", Kind = "method", Signature = "() -> Task" }]
        };
        
        var optionsType = new DotNet.TypeInfo
        {
            Name = "ChatOptions",
            Kind = "class",
            Members = [new MemberInfo { Name = "MaxTokens", Kind = "property", Signature = "int" }]
        };
        
        var modelType = new DotNet.TypeInfo
        {
            Name = "ChatMessage",
            Kind = "class",
            Members = [new MemberInfo { Name = "Content", Kind = "property", Signature = "string" }]
        };
        
        Assert.True(clientType.TruncationPriority < optionsType.TruncationPriority);
        Assert.True(optionsType.TruncationPriority < modelType.TruncationPriority);
    }

    [Fact]
    public void DotNet_TypeInfo_GetReferencedTypes_ExtractsTypesFromSignatures()
    {
        var clientType = new DotNet.TypeInfo
        {
            Name = "ChatClient",
            Kind = "class",
            Base = "BaseClient",
            Members = 
            [
                new MemberInfo { Name = "SendAsync", Kind = "method", Signature = "(ChatMessage message) -> Task<ChatResponse>" },
                new MemberInfo { Name = "Options", Kind = "property", Signature = "ChatOptions" }
            ]
        };
        
        var allTypes = new HashSet<string> { "ChatClient", "BaseClient", "ChatMessage", "ChatResponse", "ChatOptions", "UnrelatedType" };
        var refs = clientType.GetReferencedTypes(allTypes);
        
        Assert.Contains("BaseClient", refs);
        Assert.Contains("ChatMessage", refs);
        Assert.Contains("ChatResponse", refs);
        Assert.Contains("ChatOptions", refs);
        Assert.DoesNotContain("UnrelatedType", refs);
    }

    [Fact]
    public void DotNet_CSharpFormatter_Format_WithBudget_TruncatesAtLimit()
    {
        var api = CreateLargeDotNetApi(50); // 50 types
        
        // Format with a small budget
        var result = CSharpFormatter.Format(api, 2000);
        
        Assert.True(result.Length <= 2100); // Allow some margin for truncation message
        Assert.Contains("truncated", result);
    }

    [Fact]
    public void DotNet_CSharpFormatter_Format_WithBudget_PrioritizesClients()
    {
        var api = CreateDotNetApiWithClientAndModels();
        
        // Small budget - should include client but truncate models
        var result = CSharpFormatter.Format(api, 1000);
        
        Assert.Contains("ChatClient", result);
    }

    [Fact]
    public void DotNet_CSharpFormatter_Format_IncludesClientDependencies()
    {
        var api = CreateDotNetApiWithClientAndDependency();
        
        // Budget enough for client + one dependency
        var result = CSharpFormatter.Format(api, 1500);
        
        // Should include both client and its dependency
        Assert.Contains("ChatClient", result);
        Assert.Contains("ChatMessage", result);
    }

    private static DotNet.ApiIndex CreateLargeDotNetApi(int typeCount)
    {
        var types = new List<DotNet.TypeInfo>();
        
        // Add one client
        types.Add(new DotNet.TypeInfo
        {
            Name = "SampleClient",
            Kind = "class",
            Members = [new MemberInfo { Name = "DoSomething", Kind = "method", Signature = "() -> void" }]
        });
        
        // Add many model types
        for (int i = 0; i < typeCount - 1; i++)
        {
            types.Add(new DotNet.TypeInfo
            {
                Name = $"Model{i}",
                Kind = "class",
                Members = [new MemberInfo { Name = "Property", Kind = "property", Signature = "string" }]
            });
        }
        
        return new DotNet.ApiIndex
        {
            Package = "TestPackage",
            Namespaces = [new NamespaceInfo { Name = "TestNamespace", Types = types }]
        };
    }

    private static DotNet.ApiIndex CreateDotNetApiWithClientAndModels()
    {
        return new DotNet.ApiIndex
        {
            Package = "TestPackage",
            Namespaces = [new NamespaceInfo 
            { 
                Name = "TestNamespace", 
                Types = 
                [
                    new DotNet.TypeInfo
                    {
                        Name = "ChatClient",
                        Kind = "class",
                        Members = [new MemberInfo { Name = "Send", Kind = "method", Signature = "() -> void" }]
                    },
                    new DotNet.TypeInfo
                    {
                        Name = "Model1",
                        Kind = "class",
                        Members = [new MemberInfo { Name = "Prop", Kind = "property", Signature = "string" }]
                    },
                    new DotNet.TypeInfo
                    {
                        Name = "Model2",
                        Kind = "class",
                        Members = [new MemberInfo { Name = "Prop", Kind = "property", Signature = "string" }]
                    }
                ]
            }]
        };
    }

    private static DotNet.ApiIndex CreateDotNetApiWithClientAndDependency()
    {
        return new DotNet.ApiIndex
        {
            Package = "TestPackage",
            Namespaces = [new NamespaceInfo 
            { 
                Name = "TestNamespace", 
                Types = 
                [
                    new DotNet.TypeInfo
                    {
                        Name = "ChatClient",
                        Kind = "class",
                        Members = [new MemberInfo { Name = "Send", Kind = "method", Signature = "(ChatMessage msg) -> void" }]
                    },
                    new DotNet.TypeInfo
                    {
                        Name = "ChatMessage",
                        Kind = "class",
                        Members = [new MemberInfo { Name = "Content", Kind = "property", Signature = "string" }]
                    }
                ]
            }]
        };
    }

    #endregion

    #region Python Smart Truncation Tests

    [Fact]
    public void Python_ClassInfo_IsClientType_DetectsClientClasses()
    {
        var clientClass = new Python.ClassInfo(
            Name: "ChatClient",
            Base: null,
            Doc: null,
            Methods: [new Python.MethodInfo("send", "self, message", null, null, null, null)],
            Properties: null
        );
        
        var modelClass = new Python.ClassInfo(
            Name: "ChatMessage",
            Base: null,
            Doc: null,
            Methods: null,
            Properties: [new Python.PropertyInfo("content", "str", null)]
        );
        
        Assert.True(clientClass.IsClientType);
        Assert.False(modelClass.IsClientType);
    }

    [Fact]
    public void Python_ClassInfo_TruncationPriority_ClientsFirst()
    {
        var clientClass = new Python.ClassInfo(
            Name: "ChatClient",
            Base: null,
            Doc: null,
            Methods: [new Python.MethodInfo("send", "self", null, null, null, null)],
            Properties: null
        );
        
        var modelClass = new Python.ClassInfo(
            Name: "ChatMessage",
            Base: null,
            Doc: null,
            Methods: null,
            Properties: [new Python.PropertyInfo("content", "str", null)]
        );
        
        Assert.True(clientClass.TruncationPriority < modelClass.TruncationPriority);
    }

    [Fact]
    public void Python_ClassInfo_GetReferencedTypes_ExtractsFromAnnotations()
    {
        var clientClass = new Python.ClassInfo(
            Name: "ChatClient",
            Base: "BaseClient",
            Doc: null,
            Methods: [new Python.MethodInfo("send", "self, message: ChatMessage -> ChatResponse", null, null, null, null)],
            Properties: null
        );
        
        var allTypes = new HashSet<string> { "ChatClient", "BaseClient", "ChatMessage", "ChatResponse", "Unrelated" };
        var refs = clientClass.GetReferencedTypes(allTypes);
        
        Assert.Contains("BaseClient", refs);
        Assert.Contains("ChatMessage", refs);
        Assert.Contains("ChatResponse", refs);
        Assert.DoesNotContain("Unrelated", refs);
    }

    [Fact]
    public void Python_PythonFormatter_Format_WithBudget_TruncatesAtLimit()
    {
        var api = CreateLargePythonApi(50);
        
        var result = PythonFormatter.Format(api, 2000);
        
        Assert.True(result.Length <= 2100);
        Assert.Contains("truncated", result);
    }

    private static Python.ApiIndex CreateLargePythonApi(int classCount)
    {
        var classes = new List<Python.ClassInfo>();
        
        classes.Add(new Python.ClassInfo(
            Name: "SampleClient",
            Base: null,
            Doc: null,
            Methods: [new Python.MethodInfo("do_something", "self, param1: str, param2: int", null, null, null, null)],
            Properties: null
        ));
        
        for (int i = 0; i < classCount - 1; i++)
        {
            // Create more substantial model classes to trigger truncation
            classes.Add(new Python.ClassInfo(
                Name: $"Model{i}WithLongNameToTakeUpSpace",
                Base: null,
                Doc: $"This is a documentation comment for Model{i}.",
                Methods: null,
                Properties: [
                    new Python.PropertyInfo($"property_one_{i}", "str", null),
                    new Python.PropertyInfo($"property_two_{i}", "int", null),
                    new Python.PropertyInfo($"property_three_{i}", "bool", null)
                ]
            ));
        }
        
        return new Python.ApiIndex(
            Package: "test_package",
            Modules: [new Python.ModuleInfo("test_module", classes, null)]
        );
    }

    #endregion

    #region Java Smart Truncation Tests

    [Fact]
    public void Java_ClassInfo_IsClientType_DetectsClientAndBuilderClasses()
    {
        var clientClass = new Java.ClassInfo
        {
            Name = "ChatClient",
            Methods = [new Java.MethodInfo { Name = "send", Sig = "()", Ret = "void" }]
        };
        
        var builderClass = new Java.ClassInfo
        {
            Name = "ChatClientBuilder",
            Methods = [new Java.MethodInfo { Name = "build", Sig = "()", Ret = "ChatClient" }]
        };
        
        Assert.True(clientClass.IsClientType);
        Assert.True(builderClass.IsClientType); // Java includes Builder pattern
    }

    [Fact]
    public void Java_ClassInfo_GetReferencedTypes_ExtractsFromSignatures()
    {
        var clientClass = new Java.ClassInfo
        {
            Name = "ChatClient",
            Extends = "BaseClient",
            Methods = [new Java.MethodInfo { Name = "send", Sig = "(ChatMessage message)", Ret = "ChatResponse" }]
        };
        
        var allTypes = new HashSet<string> { "ChatClient", "BaseClient", "ChatMessage", "ChatResponse" };
        var refs = clientClass.GetReferencedTypes(allTypes);
        
        Assert.Contains("BaseClient", refs);
        Assert.Contains("ChatMessage", refs);
        Assert.Contains("ChatResponse", refs);
    }

    [Fact]
    public void Java_JavaFormatter_Format_WithBudget_TruncatesAtLimit()
    {
        var api = CreateLargeJavaApi(50);
        
        var result = JavaFormatter.Format(api, 2000);
        
        Assert.True(result.Length <= 2100);
        Assert.Contains("truncated", result);
    }

    private static Java.ApiIndex CreateLargeJavaApi(int classCount)
    {
        var classes = new List<Java.ClassInfo>();
        
        classes.Add(new Java.ClassInfo
        {
            Name = "SampleClient",
            Methods = [new Java.MethodInfo { Name = "doSomething", Sig = "(String param1, int param2)", Ret = "void" }]
        });
        
        for (int i = 0; i < classCount - 1; i++)
        {
            // Create more substantial model classes to trigger truncation
            classes.Add(new Java.ClassInfo
            {
                Name = $"Model{i}WithLongNameToTakeUpSpace",
                Doc = $"This is a documentation comment for Model{i}.",
                Fields = [
                    new Java.FieldInfo { Name = $"propertyOne{i}", Type = "String" },
                    new Java.FieldInfo { Name = $"propertyTwo{i}", Type = "int" },
                    new Java.FieldInfo { Name = $"propertyThree{i}", Type = "boolean" }
                ]
            });
        }
        
        return new Java.ApiIndex
        {
            Package = "com.test",
            Packages = [new Java.PackageInfo { Name = "com.test", Classes = classes }]
        };
    }

    #endregion

    #region Go Smart Truncation Tests

    [Fact]
    public void Go_StructApi_IsClientType_DetectsClientStructs()
    {
        var clientStruct = new Go.StructApi
        {
            Name = "ChatClient",
            Methods = [new Go.FuncApi { Name = "Send", Sig = "(ctx context.Context)", Ret = "error" }]
        };
        
        var modelStruct = new Go.StructApi
        {
            Name = "ChatMessage",
            Fields = [new Go.FieldApi { Name = "Content", Type = "string" }]
        };
        
        Assert.True(clientStruct.IsClientType);
        Assert.False(modelStruct.IsClientType);
    }

    [Fact]
    public void Go_StructApi_TruncationPriority_ClientsFirst()
    {
        var clientStruct = new Go.StructApi
        {
            Name = "ChatClient",
            Methods = [new Go.FuncApi { Name = "Send", Sig = "()", Ret = "error" }]
        };
        
        var optionsStruct = new Go.StructApi
        {
            Name = "ChatClientOptions",
            Fields = [new Go.FieldApi { Name = "MaxTokens", Type = "int" }]
        };
        
        Assert.True(clientStruct.TruncationPriority < optionsStruct.TruncationPriority);
    }

    [Fact]
    public void Go_GoFormatter_Format_WithBudget_TruncatesAtLimit()
    {
        var api = CreateLargeGoApi(50);
        
        var result = GoFormatter.Format(api, 2000);
        
        Assert.True(result.Length <= 2100);
        Assert.Contains("truncated", result);
    }

    private static Go.ApiIndex CreateLargeGoApi(int structCount)
    {
        var structs = new List<Go.StructApi>();
        
        structs.Add(new Go.StructApi
        {
            Name = "SampleClient",
            Methods = [new Go.FuncApi { Name = "DoSomething", Sig = "()", Ret = "error" }]
        });
        
        for (int i = 0; i < structCount - 1; i++)
        {
            structs.Add(new Go.StructApi
            {
                Name = $"Model{i}",
                Fields = [new Go.FieldApi { Name = "Prop", Type = "string" }]
            });
        }
        
        return new Go.ApiIndex
        {
            Package = "testpkg",
            Packages = [new Go.PackageApi { Name = "testpkg", Structs = structs }]
        };
    }

    #endregion

    #region TypeScript Smart Truncation Tests

    [Fact]
    public void TypeScript_ClassInfo_IsClientType_DetectsClientClasses()
    {
        var clientClass = new TypeScript.ClassInfo
        {
            Name = "ChatClient",
            Methods = [new TypeScript.MethodInfo { Name = "send", Sig = "message: string", Ret = "Promise<void>" }]
        };
        
        var modelClass = new TypeScript.ClassInfo
        {
            Name = "ChatMessage",
            Properties = [new TypeScript.PropertyInfo { Name = "content", Type = "string" }]
        };
        
        Assert.True(clientClass.IsClientType);
        Assert.False(modelClass.IsClientType);
    }

    [Fact]
    public void TypeScript_ClassInfo_GetReferencedTypes_ExtractsFromSignatures()
    {
        var clientClass = new TypeScript.ClassInfo
        {
            Name = "ChatClient",
            Extends = "BaseClient",
            Methods = [new TypeScript.MethodInfo { Name = "send", Sig = "message: ChatMessage", Ret = "Promise<ChatResponse>" }]
        };
        
        var allTypes = new HashSet<string> { "ChatClient", "BaseClient", "ChatMessage", "ChatResponse" };
        var refs = clientClass.GetReferencedTypes(allTypes);
        
        Assert.Contains("BaseClient", refs);
        Assert.Contains("ChatMessage", refs);
        Assert.Contains("ChatResponse", refs);
    }

    [Fact]
    public void TypeScript_TypeScriptFormatter_Format_WithBudget_TruncatesAtLimit()
    {
        var api = CreateLargeTypeScriptApi(50);
        
        var result = TypeScriptFormatter.Format(api, 2000);
        
        Assert.True(result.Length <= 2100);
        Assert.Contains("truncated", result);
    }

    private static TypeScript.ApiIndex CreateLargeTypeScriptApi(int classCount)
    {
        var classes = new List<TypeScript.ClassInfo>();
        
        classes.Add(new TypeScript.ClassInfo
        {
            Name = "SampleClient",
            Methods = [new TypeScript.MethodInfo { Name = "doSomething", Sig = "", Ret = "void" }]
        });
        
        for (int i = 0; i < classCount - 1; i++)
        {
            classes.Add(new TypeScript.ClassInfo
            {
                Name = $"Model{i}",
                Properties = [new TypeScript.PropertyInfo { Name = "prop", Type = "string" }]
            });
        }
        
        return new TypeScript.ApiIndex
        {
            Package = "@test/package",
            Modules = [new TypeScript.ModuleInfo { Name = "index", Classes = classes }]
        };
    }

    #endregion

    #region Cross-Language Consistency Tests

    [Fact]
    public void AllLanguages_ClientPatternDetection_ConsistentAcrossLanguages()
    {
        // All languages should detect *Client, *Service, *Manager as client types
        
        var dotnetClient = new DotNet.TypeInfo
        {
            Name = "ChatService",
            Kind = "class",
            Members = [new MemberInfo { Name = "Send", Kind = "method", Signature = "() -> void" }]
        };
        
        var pythonClient = new Python.ClassInfo(
            Name: "ChatManager",
            Base: null,
            Doc: null,
            Methods: [new Python.MethodInfo("send", "self", null, null, null, null)],
            Properties: null
        );
        
        var javaClient = new Java.ClassInfo
        {
            Name = "ChatClient",
            Methods = [new Java.MethodInfo { Name = "send", Sig = "()", Ret = "void" }]
        };
        
        var goClient = new Go.StructApi
        {
            Name = "ChatClient",
            Methods = [new Go.FuncApi { Name = "Send", Sig = "()", Ret = "error" }]
        };
        
        var tsClient = new TypeScript.ClassInfo
        {
            Name = "ChatClient",
            Methods = [new TypeScript.MethodInfo { Name = "send", Sig = "", Ret = "void" }]
        };
        
        Assert.True(dotnetClient.IsClientType);
        Assert.True(pythonClient.IsClientType);
        Assert.True(javaClient.IsClientType);
        Assert.True(goClient.IsClientType);
        Assert.True(tsClient.IsClientType);
    }

    [Fact]
    public void AllLanguages_TruncationMessage_ConsistentFormat()
    {
        // All formatters should include "truncated" in their output when over budget
        
        var dotnetResult = CSharpFormatter.Format(CreateLargeDotNetApi(50), 1000);
        var pythonResult = PythonFormatter.Format(CreateLargePythonApi(50), 1000);
        var javaResult = JavaFormatter.Format(CreateLargeJavaApi(50), 1000);
        var goResult = GoFormatter.Format(CreateLargeGoApi(50), 1000);
        var tsResult = TypeScriptFormatter.Format(CreateLargeTypeScriptApi(50), 1000);
        
        Assert.Contains("truncated", dotnetResult);
        Assert.Contains("truncated", pythonResult);
        Assert.Contains("truncated", javaResult);
        Assert.Contains("truncated", goResult);
        Assert.Contains("truncated", tsResult);
    }

    #endregion
}
