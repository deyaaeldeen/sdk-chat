using Sdk.Tools.Chat.Models;

namespace Sdk.Tools.Chat.Services.Languages;

public class JavaScriptLanguageService : LanguageService
{
    public override SdkLanguage Language => SdkLanguage.JavaScript;
    public override string FileExtension => ".js";
    public override string[] DefaultSourceDirectories => new[] { "src", "lib" };
    public override string[] DefaultIncludePatterns => new[] { "**/*.js", "**/*.mjs", "**/*.cjs" };
    public override string[] DefaultExcludePatterns => new[] 
    { 
        "**/node_modules/**", 
        "**/dist/**", 
        "**/build/**",
        "**/*.test.js",
        "**/*.spec.js"
    };
}
