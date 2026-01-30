using Sdk.Tools.Chat.Models;

namespace Sdk.Tools.Chat.Services.Languages;

public class TypeScriptLanguageService : LanguageService
{
    public override SdkLanguage Language => SdkLanguage.TypeScript;
    public override string FileExtension => ".ts";
    public override string[] DefaultSourceDirectories => new[] { "src" };
    public override string[] DefaultIncludePatterns => new[] { "**/*.ts" };
    public override string[] DefaultExcludePatterns => new[] 
    { 
        "**/node_modules/**", 
        "**/dist/**", 
        "**/*.d.ts",
        "**/*.test.ts",
        "**/*.spec.ts"
    };
}
