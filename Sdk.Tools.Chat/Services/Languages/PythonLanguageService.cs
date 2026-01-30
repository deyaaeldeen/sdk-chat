using Sdk.Tools.Chat.Models;

namespace Sdk.Tools.Chat.Services.Languages;

public class PythonLanguageService : LanguageService
{
    public override SdkLanguage Language => SdkLanguage.Python;
    public override string FileExtension => ".py";
    public override string[] DefaultSourceDirectories => new[] { "src", "." };
    public override string[] DefaultIncludePatterns => new[] { "**/*.py" };
    public override string[] DefaultExcludePatterns => new[] 
    { 
        "**/__pycache__/**", 
        "**/.*", 
        "**/test*/**",
        "**/venv/**",
        "**/.venv/**"
    };
}
