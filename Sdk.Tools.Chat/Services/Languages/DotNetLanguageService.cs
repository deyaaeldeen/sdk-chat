using Sdk.Tools.Chat.Models;

namespace Sdk.Tools.Chat.Services.Languages;

public class DotNetLanguageService : LanguageService
{
    public override SdkLanguage Language => SdkLanguage.DotNet;
    public override string FileExtension => ".cs";
    public override string[] DefaultSourceDirectories => new[] { "src" };
    public override string[] DefaultIncludePatterns => new[] { "**/*.cs" };
    public override string[] DefaultExcludePatterns => new[] 
    { 
        "**/obj/**", 
        "**/bin/**", 
        "**/*.Designer.cs",
        "**/AssemblyInfo.cs"
    };
}
