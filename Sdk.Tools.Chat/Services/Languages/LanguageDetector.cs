using Sdk.Tools.Chat.Models;
using Sdk.Tools.Chat.Services;

namespace Sdk.Tools.Chat.Services.Languages;

/// <summary>
/// Detects SDK language from package structure.
/// This is a thin wrapper around SdkInfo for backward compatibility.
/// Consider using SdkInfo directly for new code.
/// </summary>
public class LanguageDetector
{
    private static readonly Dictionary<SdkLanguage, string> LanguageNames = new()
    {
        { SdkLanguage.DotNet, ".NET" },
        { SdkLanguage.Python, "Python" },
        { SdkLanguage.JavaScript, "JavaScript" },
        { SdkLanguage.TypeScript, "TypeScript" },
        { SdkLanguage.Java, "Java" },
        { SdkLanguage.Go, "Go" }
    };
    
    private static readonly Dictionary<SdkLanguage, string> FileExtensions = new()
    {
        { SdkLanguage.DotNet, ".cs" },
        { SdkLanguage.Python, ".py" },
        { SdkLanguage.JavaScript, ".js" },
        { SdkLanguage.TypeScript, ".ts" },
        { SdkLanguage.Java, ".java" },
        { SdkLanguage.Go, ".go" }
    };
    
    /// <summary>Sync detection - returns raw enum.</summary>
    public SdkLanguage? DetectLanguage(string packagePath) => SdkInfo.DetectLanguage(packagePath);
    
    /// <summary>Async detection - returns LanguageInfo with metadata.</summary>
    public Task<LanguageInfo?> DetectAsync(string packagePath)
    {
        var lang = SdkInfo.DetectLanguage(packagePath);
        if (lang == null || !LanguageNames.ContainsKey(lang.Value)) 
            return Task.FromResult<LanguageInfo?>(null);
        
        var info = new LanguageInfo(
            lang.Value,
            LanguageNames[lang.Value],
            FileExtensions[lang.Value]
        );
        return Task.FromResult<LanguageInfo?>(info);
    }
}
