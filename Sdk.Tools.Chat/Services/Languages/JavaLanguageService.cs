using Sdk.Tools.Chat.Models;

namespace Sdk.Tools.Chat.Services.Languages;

public class JavaLanguageService : LanguageService
{
    public override SdkLanguage Language => SdkLanguage.Java;
    public override string FileExtension => ".java";
    public override string[] DefaultSourceDirectories => new[] { "src/main/java", "src" };
    public override string[] DefaultIncludePatterns => new[] { "**/*.java" };
    public override string[] DefaultExcludePatterns => new[] 
    { 
        "**/target/**", 
        "**/build/**", 
        "**/test/**",
        "**/*Test.java"
    };
}
