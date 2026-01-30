using Sdk.Tools.Chat.Models;

namespace Sdk.Tools.Chat.Services.Languages;

/// <summary>
/// Base class for language-specific SDK services.
/// Provides common configuration for source file discovery.
/// </summary>
public abstract class LanguageService
{
    /// <summary>The language this service handles.</summary>
    public abstract SdkLanguage Language { get; }
    
    /// <summary>Primary file extension for this language (e.g., ".cs").</summary>
    public abstract string FileExtension { get; }
    
    /// <summary>Default source directories to search (e.g., "src").</summary>
    public abstract string[] DefaultSourceDirectories { get; }
    
    /// <summary>Glob patterns for files to include (e.g., "**/*.cs").</summary>
    public abstract string[] DefaultIncludePatterns { get; }
    
    /// <summary>Glob patterns for files to exclude (e.g., "**/obj/**").</summary>
    public abstract string[] DefaultExcludePatterns { get; }
}
