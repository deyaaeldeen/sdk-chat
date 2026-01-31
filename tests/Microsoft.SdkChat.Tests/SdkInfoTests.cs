using Microsoft.SdkChat.Models;
using Microsoft.SdkChat.Services;
using Xunit;

namespace Microsoft.SdkChat.Tests;

public class SdkInfoTests
{
    private readonly string _testRoot;
    
    public SdkInfoTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"SdkInfoTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
    }
    
    ~SdkInfoTests()
    {
        try
        {
            if (Directory.Exists(_testRoot))
                Directory.Delete(_testRoot, recursive: true);
        }
        catch { }
    }
    
    [Fact]
    public void Scan_DotNetProject_DetectsLanguageAndSourceFolder()
    {
        // Arrange
        var srcDir = Path.Combine(_testRoot, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "MyProject.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(srcDir, "Program.cs"), "class Program { }");
        
        // Act
        var info = SdkInfo.Scan(_testRoot);
        
        // Assert
        Assert.Equal(SdkLanguage.DotNet, info.Language);
        Assert.Equal("dotnet", info.LanguageName);
        Assert.Equal(".cs", info.FileExtension);
        Assert.Equal(srcDir, info.SourceFolder);
        Assert.True(info.IsValid);
    }
    
    [Fact]
    public void Scan_DotNetProject_WithNestedSourceFiles_DetectsLanguage()
    {
        // Arrange - openai-dotnet style: .csproj in src, .cs files in src/Generated
        var srcDir = Path.Combine(_testRoot, "src");
        var generatedDir = Path.Combine(srcDir, "Generated");
        Directory.CreateDirectory(generatedDir);
        File.WriteAllText(Path.Combine(srcDir, "OpenAI.csproj"), "<Project />");
        // No .cs files at src level, all in Generated subfolder
        File.WriteAllText(Path.Combine(generatedDir, "Client.cs"), "class Client { }");
        File.WriteAllText(Path.Combine(generatedDir, "Models.cs"), "class Models { }");
        
        // Act
        var info = SdkInfo.Scan(_testRoot);
        
        // Assert - should still detect as dotnet
        Assert.Equal(SdkLanguage.DotNet, info.Language);
        Assert.Equal(srcDir, info.SourceFolder);
    }
    
    [Fact]
    public void Scan_PythonProject_DetectsLanguageAndSourceFolder()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_testRoot, "pyproject.toml"), "[project]");
        var srcDir = Path.Combine(_testRoot, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "main.py"), "print('hello')");
        
        // Act
        var info = SdkInfo.Scan(_testRoot);
        
        // Assert
        Assert.Equal(SdkLanguage.Python, info.Language);
        Assert.Equal("python", info.LanguageName);
        Assert.Equal(".py", info.FileExtension);
        Assert.Equal(srcDir, info.SourceFolder);
    }
    
    [Fact]
    public void Scan_PythonFlatModule_PrefersRootWithMostFiles()
    {
        // Arrange - Python flat module: .py files at root (like openai-python)
        File.WriteAllText(Path.Combine(_testRoot, "pyproject.toml"), "[project]");
        
        // Create multiple .py files at root
        File.WriteAllText(Path.Combine(_testRoot, "__init__.py"), "");
        File.WriteAllText(Path.Combine(_testRoot, "client.py"), "class Client: pass");
        File.WriteAllText(Path.Combine(_testRoot, "models.py"), "class Model: pass");
        File.WriteAllText(Path.Combine(_testRoot, "api.py"), "def call(): pass");
        
        // Create src folder with fewer files
        var srcDir = Path.Combine(_testRoot, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "helper.py"), "def help(): pass");
        
        // Act
        var info = SdkInfo.Scan(_testRoot);
        
        // Assert - should pick root (4 files) over src (1 file)
        Assert.Equal(SdkLanguage.Python, info.Language);
        Assert.Equal(_testRoot, info.SourceFolder);
    }
    
    [Fact]
    public void Scan_JavaProject_DetectsLanguageAndSourceFolder()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_testRoot, "pom.xml"), "<project />");
        var srcDir = Path.Combine(_testRoot, "src", "main", "java");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "Main.java"), "class Main { }");
        
        // Act
        var info = SdkInfo.Scan(_testRoot);
        
        // Assert
        Assert.Equal(SdkLanguage.Java, info.Language);
        Assert.Equal("java", info.LanguageName);
        Assert.Equal(".java", info.FileExtension);
        Assert.Equal(srcDir, info.SourceFolder);
    }
    
    [Fact]
    public void Scan_JavaMultiModuleGradle_DetectsLanguage()
    {
        // Arrange - Multi-module Gradle project like openai-java
        // Structure: root/build.gradle.kts, root/module-core/src/main/java/*.java
        File.WriteAllText(Path.Combine(_testRoot, "build.gradle.kts"), "plugins { }");
        File.WriteAllText(Path.Combine(_testRoot, "settings.gradle.kts"), "include(\":module-core\")");
        
        // Create module-core with Java source
        var moduleCore = Path.Combine(_testRoot, "module-core");
        var srcDir = Path.Combine(moduleCore, "src", "main", "java");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(moduleCore, "build.gradle.kts"), "plugins { java }");
        File.WriteAllText(Path.Combine(srcDir, "Client.java"), "public class Client { }");
        File.WriteAllText(Path.Combine(srcDir, "Model.java"), "public class Model { }");
        
        // Act
        var info = SdkInfo.Scan(_testRoot);
        
        // Assert - should detect Java and find source in submodule
        Assert.Equal(SdkLanguage.Java, info.Language);
        Assert.Equal("java", info.LanguageName);
        Assert.Equal(".java", info.FileExtension);
        Assert.Equal(srcDir, info.SourceFolder);
    }
    
    [Fact]
    public void Scan_JavaMultiModuleMaven_DetectsLanguage()
    {
        // Arrange - Multi-module Maven project
        // Structure: root/pom.xml, root/sdk-core/src/main/java/*.java
        File.WriteAllText(Path.Combine(_testRoot, "pom.xml"), "<project><modules><module>sdk-core</module></modules></project>");
        
        // Create sdk-core module with Java source
        var sdkCore = Path.Combine(_testRoot, "sdk-core");
        var srcDir = Path.Combine(sdkCore, "src", "main", "java");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(sdkCore, "pom.xml"), "<project />");
        File.WriteAllText(Path.Combine(srcDir, "Api.java"), "public class Api { }");
        File.WriteAllText(Path.Combine(srcDir, "Service.java"), "public class Service { }");
        File.WriteAllText(Path.Combine(srcDir, "Config.java"), "public class Config { }");
        
        // Act
        var info = SdkInfo.Scan(_testRoot);
        
        // Assert - should detect Java and find source in submodule
        Assert.Equal(SdkLanguage.Java, info.Language);
        Assert.Equal(srcDir, info.SourceFolder);
    }
    
    [Fact]
    public void Scan_JavaMultiModule_PicksModuleWithMostFiles()
    {
        // Arrange - Multi-module project with multiple source modules
        File.WriteAllText(Path.Combine(_testRoot, "build.gradle.kts"), "");
        
        // Create module-a with 2 files
        var moduleA = Path.Combine(_testRoot, "module-a");
        var srcDirA = Path.Combine(moduleA, "src", "main", "java");
        Directory.CreateDirectory(srcDirA);
        File.WriteAllText(Path.Combine(srcDirA, "A1.java"), "class A1 {}");
        File.WriteAllText(Path.Combine(srcDirA, "A2.java"), "class A2 {}");
        
        // Create module-b with 5 files (should be picked)
        var moduleB = Path.Combine(_testRoot, "module-b");
        var srcDirB = Path.Combine(moduleB, "src", "main", "java");
        Directory.CreateDirectory(srcDirB);
        for (int i = 1; i <= 5; i++)
            File.WriteAllText(Path.Combine(srcDirB, $"B{i}.java"), $"class B{i} {{}}");
        
        // Act
        var info = SdkInfo.Scan(_testRoot);
        
        // Assert - module-b has more files, should be picked
        Assert.Equal(SdkLanguage.Java, info.Language);
        Assert.Equal(srcDirB, info.SourceFolder);
    }
    
    [Fact]
    public void Scan_TypeScriptProject_DetectsLanguageAndSourceFolder()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_testRoot, "tsconfig.json"), "{}");
        File.WriteAllText(Path.Combine(_testRoot, "package.json"), "{}");
        var srcDir = Path.Combine(_testRoot, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "index.ts"), "export {}");
        
        // Act
        var info = SdkInfo.Scan(_testRoot);
        
        // Assert
        Assert.Equal(SdkLanguage.TypeScript, info.Language);
        Assert.Equal("typescript", info.LanguageName);
        Assert.Equal(".ts", info.FileExtension);
    }
    
    [Fact]
    public void Scan_JavaScriptProject_WithoutTsConfig_DetectsJavaScript()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_testRoot, "package.json"), "{}");
        var srcDir = Path.Combine(_testRoot, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "index.js"), "module.exports = {}");
        
        // Act
        var info = SdkInfo.Scan(_testRoot);
        
        // Assert
        Assert.Equal(SdkLanguage.JavaScript, info.Language);
        Assert.Equal("javascript", info.LanguageName);
        Assert.Equal(".js", info.FileExtension);
    }
    
    [Fact]
    public void Scan_GoProject_DetectsLanguageAndSourceFolder()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_testRoot, "go.mod"), "module test");
        var pkgDir = Path.Combine(_testRoot, "pkg");
        Directory.CreateDirectory(pkgDir);
        File.WriteAllText(Path.Combine(pkgDir, "main.go"), "package main");
        
        // Act
        var info = SdkInfo.Scan(_testRoot);
        
        // Assert
        Assert.Equal(SdkLanguage.Go, info.Language);
        Assert.Equal("go", info.LanguageName);
        Assert.Equal(".go", info.FileExtension);
    }
    
    [Fact]
    public void Scan_GoFlatModule_PrefersRootOverInternal()
    {
        // Arrange - go-openai style: most .go files at root, internal folder exists
        File.WriteAllText(Path.Combine(_testRoot, "go.mod"), "module github.com/example/sdk");
        
        // Create many .go files at root (simulating flat module pattern)
        File.WriteAllText(Path.Combine(_testRoot, "client.go"), "package sdk");
        File.WriteAllText(Path.Combine(_testRoot, "chat.go"), "package sdk");
        File.WriteAllText(Path.Combine(_testRoot, "completion.go"), "package sdk");
        File.WriteAllText(Path.Combine(_testRoot, "embeddings.go"), "package sdk");
        File.WriteAllText(Path.Combine(_testRoot, "models.go"), "package sdk");
        
        // Create internal folder with fewer files
        var internalDir = Path.Combine(_testRoot, "internal");
        Directory.CreateDirectory(internalDir);
        File.WriteAllText(Path.Combine(internalDir, "helper.go"), "package internal");
        
        // Act
        var info = SdkInfo.Scan(_testRoot);
        
        // Assert - should pick root (5 files) over internal (1 file)
        Assert.Equal(SdkLanguage.Go, info.Language);
        Assert.Equal(_testRoot, info.SourceFolder);
    }
    
    [Fact]
    public void Scan_GoFlatModule_WithExamples_FindsSamplesFolder()
    {
        // Arrange - flat module with examples folder
        File.WriteAllText(Path.Combine(_testRoot, "go.mod"), "module github.com/example/sdk");
        File.WriteAllText(Path.Combine(_testRoot, "client.go"), "package sdk");
        File.WriteAllText(Path.Combine(_testRoot, "chat.go"), "package sdk");
        
        var examplesDir = Path.Combine(_testRoot, "examples");
        Directory.CreateDirectory(examplesDir);
        File.WriteAllText(Path.Combine(examplesDir, "basic.go"), "package main");
        File.WriteAllText(Path.Combine(examplesDir, "advanced.go"), "package main");
        
        // Act
        var info = SdkInfo.Scan(_testRoot);
        
        // Assert
        Assert.Equal(_testRoot, info.SourceFolder);
        Assert.Equal(examplesDir, info.SamplesFolder);
    }
    
    [Fact]
    public void Scan_GoWithOnlyInternal_UsesInternal()
    {
        // Arrange - Go project with only internal folder (no root .go files)
        File.WriteAllText(Path.Combine(_testRoot, "go.mod"), "module test");
        
        var internalDir = Path.Combine(_testRoot, "internal");
        Directory.CreateDirectory(internalDir);
        File.WriteAllText(Path.Combine(internalDir, "helper.go"), "package internal");
        File.WriteAllText(Path.Combine(internalDir, "utils.go"), "package internal");
        
        // Act
        var info = SdkInfo.Scan(_testRoot);
        
        // Assert - should use internal since no root .go files
        Assert.Equal(SdkLanguage.Go, info.Language);
        Assert.Equal(internalDir, info.SourceFolder);
    }
    
    [Fact]
    public void Scan_GoPkgPattern_UsesPkg()
    {
        // Arrange - Go project with pkg folder (SDK style)
        File.WriteAllText(Path.Combine(_testRoot, "go.mod"), "module test");
        
        var pkgDir = Path.Combine(_testRoot, "pkg");
        Directory.CreateDirectory(pkgDir);
        File.WriteAllText(Path.Combine(pkgDir, "client.go"), "package sdk");
        File.WriteAllText(Path.Combine(pkgDir, "models.go"), "package sdk");
        File.WriteAllText(Path.Combine(pkgDir, "operations.go"), "package sdk");
        
        // Act
        var info = SdkInfo.Scan(_testRoot);
        
        // Assert
        Assert.Equal(SdkLanguage.Go, info.Language);
        Assert.Equal(pkgDir, info.SourceFolder);
    }
    
    [Fact]
    public void Scan_WithExamplesFolder_FindsSamplesFolder()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_testRoot, "pyproject.toml"), "[project]");
        File.WriteAllText(Path.Combine(_testRoot, "main.py"), "print('hello')");
        var examplesDir = Path.Combine(_testRoot, "examples");
        Directory.CreateDirectory(examplesDir);
        File.WriteAllText(Path.Combine(examplesDir, "sample.py"), "print('sample')");
        
        // Act
        var info = SdkInfo.Scan(_testRoot);
        
        // Assert
        Assert.Equal(examplesDir, info.SamplesFolder);
        Assert.Equal(examplesDir, info.SuggestedSamplesFolder);
        Assert.Contains(examplesDir, info.AllSamplesCandidates);
    }
    
    [Fact]
    public void Scan_WithSamplesFolder_FindsSamplesFolder()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_testRoot, "setup.py"), "");
        File.WriteAllText(Path.Combine(_testRoot, "main.py"), "print('hello')");
        var samplesDir = Path.Combine(_testRoot, "examples");
        Directory.CreateDirectory(samplesDir);
        File.WriteAllText(Path.Combine(samplesDir, "sample.py"), "print('sample')");
        
        // Act
        var info = SdkInfo.Scan(_testRoot);
        
        // Assert
        Assert.Equal(samplesDir, info.SamplesFolder);
    }
    
    [Fact]
    public void Scan_WithoutSamplesFolder_SuggestsExamples()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_testRoot, "setup.py"), "");
        File.WriteAllText(Path.Combine(_testRoot, "main.py"), "print('hello')");
        
        // Act
        var info = SdkInfo.Scan(_testRoot);
        
        // Assert
        Assert.Null(info.SamplesFolder);
        Assert.Equal(Path.Combine(_testRoot, "examples"), info.SuggestedSamplesFolder);
    }
    
    [Fact]
    public void Scan_CachesResults()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_testRoot, "setup.py"), "");
        File.WriteAllText(Path.Combine(_testRoot, "main.py"), "print('hello')");
        
        // Act
        var info1 = SdkInfo.Scan(_testRoot);
        var info2 = SdkInfo.Scan(_testRoot);
        
        // Assert - same reference means cached
        Assert.Same(info1, info2);
    }
    
    [Fact]
    public void ClearCache_RemovesCachedResults()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_testRoot, "setup.py"), "");
        File.WriteAllText(Path.Combine(_testRoot, "main.py"), "print('hello')");
        var info1 = SdkInfo.Scan(_testRoot);
        
        // Act
        SdkInfo.ClearCache();
        var info2 = SdkInfo.Scan(_testRoot);
        
        // Assert - different reference means not cached
        Assert.NotSame(info1, info2);
        // But should have same values
        Assert.Equal(info1.Language, info2.Language);
    }
    
    [Fact]
    public void DetectLanguage_QuickDetection_ReturnsCorrectLanguage()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_testRoot, "setup.py"), "");
        
        // Act
        var lang = SdkInfo.DetectLanguage(_testRoot);
        
        // Assert
        Assert.Equal(SdkLanguage.Python, lang);
    }
    
    [Fact]
    public void DetectLanguage_NonExistentDirectory_ReturnsNull()
    {
        // Act
        var lang = SdkInfo.DetectLanguage("/nonexistent/path/to/sdk");
        
        // Assert
        Assert.Null(lang);
    }
    
    [Fact]
    public void DetectLanguage_EmptyDirectory_ReturnsNull()
    {
        // Act
        var lang = SdkInfo.DetectLanguage(_testRoot);
        
        // Assert
        Assert.Null(lang);
    }
    
    [Fact]
    public void Scan_BuildFileInSubdir_DetectsLanguage()
    {
        // Arrange - openai-dotnet style: .csproj in src folder
        var srcDir = Path.Combine(_testRoot, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "OpenAI.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(srcDir, "Client.cs"), "class Client { }");
        
        // Act
        var info = SdkInfo.Scan(_testRoot);
        
        // Assert
        Assert.Equal(SdkLanguage.DotNet, info.Language);
        Assert.Equal(srcDir, info.SourceFolder);
    }
    
    [Fact]
    public void Scan_MultipleSamplesFolders_PicksBestByFileCount()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_testRoot, "setup.py"), "");
        File.WriteAllText(Path.Combine(_testRoot, "main.py"), "");
        
        // Create samples folder with 1 file
        var samplesDir = Path.Combine(_testRoot, "examples");
        Directory.CreateDirectory(samplesDir);
        File.WriteAllText(Path.Combine(samplesDir, "sample1.py"), "");
        
        // Create examples folder with 5 files
        var examplesDir = Path.Combine(_testRoot, "examples");
        Directory.CreateDirectory(examplesDir);
        for (int i = 1; i <= 5; i++)
            File.WriteAllText(Path.Combine(examplesDir, $"example{i}.py"), "");
        
        // Act
        var info = SdkInfo.Scan(_testRoot);
        
        // Assert - examples has more files, should be picked
        Assert.Equal(examplesDir, info.SamplesFolder);
        Assert.Contains(samplesDir, info.AllSamplesCandidates);
        Assert.Contains(examplesDir, info.AllSamplesCandidates);
    }
    
    [Fact]
    public void Scan_GlobPatternSamplesFolder_FindsModulePrefixedExample()
    {
        // Arrange - Java multi-module project with "sdk-name-example" folder pattern
        File.WriteAllText(Path.Combine(_testRoot, "build.gradle.kts"), "");
        
        // Create sdk-java-example folder (matches *-example pattern)
        var exampleDir = Path.Combine(_testRoot, "sdk-java-example");
        var srcDir = Path.Combine(exampleDir, "src", "main", "java");
        Directory.CreateDirectory(srcDir);
        for (int i = 1; i <= 10; i++)
            File.WriteAllText(Path.Combine(srcDir, $"Example{i}.java"), $"class Example{i} {{}}");
        
        // Act
        var info = SdkInfo.Scan(_testRoot);
        
        // Assert - should find sdk-java-example as samples folder
        Assert.Contains("sdk-java-example", info.SamplesFolder!);
    }
}
