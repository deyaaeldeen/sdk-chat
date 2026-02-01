// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.SdkChat.Models;

namespace Microsoft.SdkChat.Services;

/// <summary>
/// Unified SDK detection: language, source folder, and samples folder in one scan.
/// This is the single source of truth for SDK structure detection.
/// </summary>
public class SdkInfo
{
    /// <summary>
    /// Maximum number of SDK paths to cache. Prevents memory leaks when scanning many directories.
    /// </summary>
    public const int MaxCacheSize = 100;
    
    private static readonly LruCache<string, Lazy<SdkInfo>> _cache = new(MaxCacheSize, StringComparer.OrdinalIgnoreCase);
    
    // Static enumeration options - reuse to avoid allocations
    private static readonly EnumerationOptions ShallowOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true
    };
    
    private static readonly EnumerationOptions DeepOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        MaxRecursionDepth = 5
    };
    
    private static readonly EnumerationOptions MediumOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        MaxRecursionDepth = 3
    };

    // Samples folder candidates in priority order
    private static readonly string[] SamplesFolderPatterns =
    [
        "samples",
        "examples",
        "example", 
        "sample",
        "demo",
        "demos",
        "quickstarts",
        "tests/samples",
        "docs/samples",
        "*-example",     // For patterns like openai-java-example
        "*-examples",    // For patterns like sdk-examples
        "*-samples"      // For patterns like sdk-samples
    ];
    
    // Language-specific patterns - order matters (more specific first)
    private static readonly LanguagePattern[] LanguagePatterns =
    [
        // .NET
        new(SdkLanguage.DotNet, "dotnet", ".cs",
            new[] { "*.csproj", "*.sln" }, 
            new[] { "src", "lib", "source" }),
        
        // Python
        new(SdkLanguage.Python, "python", ".py",
            new[] { "setup.py", "pyproject.toml" },
            new[] { "src", "lib", "." }),
        
        // Java
        new(SdkLanguage.Java, "java", ".java",
            new[] { "pom.xml", "build.gradle", "build.gradle.kts" },
            new[] { "src/main/java", "src", "*/src/main/java", "*/src" }),
        
        // TypeScript (before JavaScript - has tsconfig.json)
        new(SdkLanguage.TypeScript, "typescript", ".ts",
            new[] { "tsconfig.json" },
            new[] { "src", "lib" }),
        
        // JavaScript (package.json but typically no tsconfig)
        new(SdkLanguage.JavaScript, "javascript", ".js",
            new[] { "package.json" },
            new[] { "src", "lib" }),
        
        // Go
        new(SdkLanguage.Go, "go", ".go",
            new[] { "go.mod" },
            new[] { "pkg", "internal", "cmd", "." })
    ];
    
    /// <summary>
    /// Folders to exclude from source and samples enumeration.
    /// These are typically build artifacts, dependencies, or version control folders
    /// that should never be scanned (e.g., node_modules can contain 100K+ files).
    /// </summary>
    public static readonly IReadOnlySet<string> ExcludedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "dist", "build", "target", 
        ".git", ".vs", ".idea", "__pycache__", ".venv", "venv",
        "vendor", "packages", "artifacts", ".nuget", 
        ".next", "coverage", "out", ".cache", ".tox", "htmlcov"
    };
    
    /// <summary>
    /// Safe enumeration options that skip excluded folders.
    /// Use this when enumerating files to avoid scanning node_modules, .git, etc.
    /// </summary>
    public static readonly EnumerationOptions SafeEnumerationOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        MaxRecursionDepth = 10,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
    };
    
    /// <summary>
    /// Safely enumerates files in a directory, skipping excluded folders like node_modules.
    /// This is the preferred method for file enumeration to avoid performance issues.
    /// </summary>
    /// <param name="directory">Root directory to enumerate.</param>
    /// <param name="searchPattern">File search pattern (e.g., "*.cs").</param>
    /// <param name="maxFiles">Maximum number of files to return (prevents runaway enumeration).</param>
    /// <returns>Enumerable of file paths, excluding files in dangerous folders.</returns>
    public static IEnumerable<string> EnumerateFilesSafely(
        string directory, 
        string searchPattern = "*.*", 
        int maxFiles = 10000)
    {
        if (!Directory.Exists(directory))
            yield break;
        
        var count = 0;
        var stack = new Stack<string>();
        stack.Push(directory);
        
        while (stack.Count > 0 && count < maxFiles)
        {
            var currentDir = stack.Pop();
            
            // Enumerate files in current directory
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(currentDir, searchPattern, SearchOption.TopDirectoryOnly);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            
            foreach (var file in files)
            {
                if (++count > maxFiles)
                    yield break;
                yield return file;
            }
            
            // Add subdirectories (excluding dangerous ones)
            IEnumerable<string> subdirs;
            try
            {
                subdirs = Directory.EnumerateDirectories(currentDir);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            
            foreach (var subdir in subdirs)
            {
                var dirName = Path.GetFileName(subdir);
                if (!ExcludedFolders.Contains(dirName))
                {
                    stack.Push(subdir);
                }
            }
        }
    }
    
    /// <summary>
    /// Safely counts files matching a pattern, skipping excluded folders.
    /// </summary>
    public static int CountFilesSafely(string directory, string searchPattern = "*.*", int maxCount = 10000)
    {
        return EnumerateFilesSafely(directory, searchPattern, maxCount).Count();
    }

    /// <summary>Root path of the SDK.</summary>
    public string RootPath { get; }
    
    /// <summary>Name of the SDK (derived from folder name).</summary>
    public string SdkName { get; }
    
    /// <summary>Detected language enum, or null if unknown.</summary>
    public SdkLanguage? Language { get; }
    
    /// <summary>Language name (e.g., "dotnet", "python").</summary>
    public string? LanguageName { get; }
    
    /// <summary>Primary file extension for this language.</summary>
    public string? FileExtension { get; }
    
    /// <summary>Path to the source code folder.</summary>
    public string SourceFolder { get; }
    
    /// <summary>Path to existing samples folder, if found.</summary>
    public string? SamplesFolder { get; }
    
    /// <summary>Suggested path for samples folder (existing or default).</summary>
    public string SuggestedSamplesFolder { get; }
    
    /// <summary>All detected samples folder candidates.</summary>
    public IReadOnlyList<string> AllSamplesCandidates { get; }
    
    /// <summary>Whether the SDK was successfully detected.</summary>
    public bool IsValid => Language != null || SourceFolder != RootPath;

    private SdkInfo(
        string rootPath,
        SdkLanguage? language,
        string? languageName,
        string? fileExtension,
        string sourceFolder,
        string? samplesFolder,
        List<string> allSamplesCandidates)
    {
        RootPath = rootPath;
        SdkName = Path.GetFileName(rootPath);
        Language = language;
        LanguageName = languageName;
        FileExtension = fileExtension;
        SourceFolder = sourceFolder;
        SamplesFolder = samplesFolder;
        SuggestedSamplesFolder = samplesFolder ?? Path.Combine(rootPath, "examples");
        AllSamplesCandidates = allSamplesCandidates.AsReadOnly();
    }
    
    /// <summary>
    /// Minimum allowed path length to prevent scanning root-level directories.
    /// On Windows: C:\ = 3 chars; on Unix: / = 1 char.
    /// We require at least 4 chars to ensure we're not at filesystem root.
    /// </summary>
    private const int MinPathLength = 4;
    
    /// <summary>
    /// Paths that should never be scanned directly (but subdirectories may be allowed).
    /// These are system directories that could cause performance issues or security risks.
    /// </summary>
    private static readonly HashSet<string> BlockedRootPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/",
        "/bin",
        "/boot",
        "/dev",
        "/etc",
        "/lib",
        "/lib64",
        "/proc",
        "/root",
        "/sbin",
        "/sys",
        "/usr",
        "/var",
        "C:\\",
        "C:\\Windows",
        "C:\\Program Files",
        "C:\\Program Files (x86)",
        "C:\\ProgramData"
    };
    
    /// <summary>
    /// Validates that a path is safe to scan.
    /// Throws ArgumentException if the path is invalid or dangerous.
    /// </summary>
    /// <param name="path">The path to validate.</param>
    /// <exception cref="ArgumentException">Thrown if the path is unsafe to scan.</exception>
    private static void ValidateScanPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("SDK path cannot be null or empty.", nameof(path));
        
        // Canonicalize the path
        var fullPath = Path.GetFullPath(path);
        
        // Check minimum length to prevent root scanning
        if (fullPath.Length < MinPathLength)
            throw new ArgumentException(
                $"Path '{fullPath}' is too short. SDK path must be at least {MinPathLength} characters to prevent root-level scanning.",
                nameof(path));
        
        // Check for blocked root paths (only exact match, not subdirectories)
        // This blocks scanning "/" or "/usr" directly, but allows "/tmp/myproject" or "/home/user/sdk"
        var normalizedPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (BlockedRootPaths.Contains(normalizedPath))
        {
            throw new ArgumentException(
                $"Scanning system directory '{fullPath}' is not allowed for security and performance reasons.",
                nameof(path));
        }
        
        // Check for path traversal attempts (.. after normalization shouldn't exist, but verify)
        if (fullPath.Contains(".."))
            throw new ArgumentException(
                $"Path '{path}' contains path traversal sequences which are not allowed.",
                nameof(path));
    }

    /// <summary>
    /// Scans the SDK root and returns detection results.
    /// Results are cached by path.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown if the path is invalid or unsafe to scan.</exception>
    public static SdkInfo Scan(string sdkRoot)
    {
        // SECURITY: Validate path before scanning
        ValidateScanPath(sdkRoot);
        
        sdkRoot = Path.GetFullPath(sdkRoot);
        return _cache.GetOrAdd(sdkRoot, path => new Lazy<SdkInfo>(() => ScanInternal(path))).Value;
    }
    
    /// <summary>
    /// Asynchronously scans the SDK root and returns detection results.
    /// Uses Task.Run to offload I/O-bound work from the calling thread.
    /// Results are cached by path.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown if the path is invalid or unsafe to scan.</exception>
    public static async ValueTask<SdkInfo> ScanAsync(string sdkRoot, CancellationToken ct = default)
    {
        // SECURITY: Validate path before scanning
        ValidateScanPath(sdkRoot);
        
        sdkRoot = Path.GetFullPath(sdkRoot);
        var lazy = _cache.GetOrAdd(sdkRoot, path => new Lazy<SdkInfo>(() => ScanInternal(path)));
        
        // If already computed, return immediately
        if (lazy.IsValueCreated)
            return lazy.Value;
        
        // Otherwise, run on thread pool to avoid blocking
        return await Task.Run(() => lazy.Value, ct).ConfigureAwait(false);
    }
    
    /// <summary>
    /// Clears the detection cache.
    /// </summary>
    public static void ClearCache() => _cache.Clear();
    
    /// <summary>
    /// Detects just the language without full folder scanning.
    /// Faster than full Scan() when you only need the language.
    /// </summary>
    public static SdkLanguage? DetectLanguage(string sdkRoot)
    {
        if (!Directory.Exists(sdkRoot))
            return null;
        
        sdkRoot = Path.GetFullPath(sdkRoot);
        
        // Check cache first
        if (_cache.TryGetValue(sdkRoot, out var cached) && cached is not null)
            return cached.Value.Language;
        
        // Quick detection without full scan
        foreach (var pattern in LanguagePatterns)
        {
            if (HasBuildMarker(sdkRoot, pattern.BuildFilePatterns))
            {
                // Special case: TypeScript vs JavaScript
                if (pattern.LanguageEnum == SdkLanguage.JavaScript &&
                    File.Exists(Path.Combine(sdkRoot, "tsconfig.json")))
                {
                    return SdkLanguage.TypeScript;
                }
                
                return pattern.LanguageEnum != SdkLanguage.Unknown ? pattern.LanguageEnum : null;
            }
        }
        
        return null;
    }

    private static SdkInfo ScanInternal(string root)
    {
        using var activity = Telemetry.SdkChatTelemetry.StartScan(root);
        try
        {
            // Detect language and source folder
            var (sourceFolder, languageEnum, languageName, fileExt) = DetectSourceFolder(root);
            
            // Detect samples folders
            var (samplesFolder, allCandidates) = DetectSamplesFolder(root);
            
            var result = new SdkInfo(
                rootPath: root,
                language: languageEnum,
                languageName: languageName,
                fileExtension: fileExt,
                sourceFolder: sourceFolder,
                samplesFolder: samplesFolder,
                allSamplesCandidates: allCandidates
            );
            
            activity?.SetTag("sdk.language", languageName ?? "unknown");
            activity?.SetTag("sdk.has_samples", samplesFolder != null);
            
            return result;
        }
        catch (UnauthorizedAccessException ex)
        {
            Telemetry.SdkChatTelemetry.RecordError(activity, ex);
            // Return minimal info if we can't access the directory
            return new SdkInfo(
                rootPath: root,
                language: null,
                languageName: null,
                fileExtension: null,
                sourceFolder: root,
                samplesFolder: null,
                allSamplesCandidates: []
            );
        }
    }
    
    private static (string SourceFolder, SdkLanguage? Language, string? LanguageName, string? FileExt) DetectSourceFolder(string root)
    {
        // Try each language pattern
        foreach (var pattern in LanguagePatterns)
        {
            if (!HasBuildMarker(root, pattern.BuildFilePatterns))
                continue;
            
            // Special case: distinguish TypeScript from JavaScript
            var actualPattern = pattern;
            if (pattern.LanguageEnum == SdkLanguage.JavaScript &&
                File.Exists(Path.Combine(root, "tsconfig.json")))
            {
                actualPattern = LanguagePatterns.First(p => p.LanguageEnum == SdkLanguage.TypeScript);
            }
            
            // Find best source folder by counting source files
            // This handles "flat module" patterns (e.g., Go projects with .go files at root)
            // and multi-module patterns (e.g., Java/Gradle projects with */src/main/java)
            string? bestCandidate = null;
            int bestCount = 0;
            
            foreach (var srcPattern in actualPattern.SourceFolderPatterns)
            {
                if (srcPattern.Contains('*'))
                {
                    // Glob pattern - expand and check each match
                    var candidates = ExpandSourceFolderGlob(root, srcPattern);
                    foreach (var candidate in candidates)
                    {
                        var count = CountSourceFiles(candidate, actualPattern.FileExtension);
                        if (count > bestCount)
                        {
                            bestCandidate = candidate;
                            bestCount = count;
                        }
                    }
                }
                else
                {
                    var candidate = srcPattern == "." ? root : Path.Combine(root, srcPattern);
                    if (!Directory.Exists(candidate))
                        continue;
                    
                    var count = CountSourceFiles(candidate, actualPattern.FileExtension);
                    if (count > bestCount)
                    {
                        bestCandidate = candidate;
                        bestCount = count;
                    }
                }
            }
            
            if (bestCandidate != null)
            {
                var langEnum = actualPattern.LanguageEnum != SdkLanguage.Unknown 
                    ? actualPattern.LanguageEnum 
                    : (SdkLanguage?)null;
                return (bestCandidate, langEnum, actualPattern.Name, actualPattern.FileExtension);
            }
        }
        
        // Fallback: look for any common source folder
        var fallbackFolders = new[] { "src", "lib", "source", "sdk", "pkg" };
        foreach (var folder in fallbackFolders)
        {
            var candidate = Path.Combine(root, folder);
            if (Directory.Exists(candidate) && HasAnySourceFiles(candidate))
            {
                return (candidate, null, null, null);
            }
        }
        
        // Last resort: use root if it has source files
        if (HasSourceFilesShallow(root))
        {
            return (root, null, null, null);
        }
        
        return (root, null, null, null);
    }
    
    /// <summary>
    /// Expands a glob pattern like "*/src/main/java" to actual directories.
    /// Supports patterns with * at the start to match submodules.
    /// </summary>
    private static IEnumerable<string> ExpandSourceFolderGlob(string root, string pattern)
    {
        // Split pattern into parts: "*/src/main/java" => ["*", "src", "main", "java"]
        var parts = pattern.Split('/', '\\');
        
        if (parts.Length == 0)
            yield break;
        
        // Start with root directories matching the first part (e.g., "*" matches all subdirs)
        IEnumerable<string> currentDirs = new[] { root };
        
        foreach (var part in parts)
        {
            List<string> nextDirs = [];
            
            foreach (var dir in currentDirs)
            {
                if (part == "*")
                {
                    // Match all immediate subdirectories (except excluded ones)
                    try
                    {
                        foreach (var subdir in Directory.EnumerateDirectories(dir))
                        {
                            var name = Path.GetFileName(subdir);
                            if (!ExcludedFolders.Contains(name))
                            {
                                nextDirs.Add(subdir);
                            }
                        }
                    }
                    catch (UnauthorizedAccessException) { }
                }
                else if (part.Contains('*'))
                {
                    // Wildcard pattern like "*-core"
                    try
                    {
                        foreach (var subdir in Directory.EnumerateDirectories(dir, part))
                        {
                            nextDirs.Add(subdir);
                        }
                    }
                    catch (UnauthorizedAccessException) { }
                }
                else
                {
                    // Literal directory name
                    var candidate = Path.Combine(dir, part);
                    if (Directory.Exists(candidate))
                    {
                        nextDirs.Add(candidate);
                    }
                }
            }
            
            currentDirs = nextDirs;
        }
        
        foreach (var dir in currentDirs)
        {
            yield return dir;
        }
    }
    
    private static (string? SamplesFolder, List<string> AllCandidates) DetectSamplesFolder(string root)
    {
        List<(string Path, int Score)> candidates = [];
        
        foreach (var pattern in SamplesFolderPatterns)
        {
            // Check if pattern contains wildcards
            if (pattern.Contains('*'))
            {
                // Use glob matching for wildcard patterns
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(root, pattern))
                    {
                        var fileCount = CountFilesQuick(dir);
                        if (fileCount > 0)
                        {
                            candidates.Add((dir, fileCount));
                        }
                    }
                }
                catch (UnauthorizedAccessException) { }
            }
            else
            {
                var candidate = Path.Combine(root, pattern);
                if (!Directory.Exists(candidate))
                    continue;
                
                var fileCount = CountFilesQuick(candidate);
                if (fileCount > 0)
                {
                    candidates.Add((candidate, fileCount));
                }
            }
        }
        
        // Sort by score descending
        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        
        var allPaths = candidates.ConvertAll(c => c.Path);
        var bestMatch = candidates.Count > 0 ? candidates[0].Path : null;
        
        return (bestMatch, allPaths);
    }
    
    private static bool HasBuildMarker(string root, string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            // Check if pattern contains wildcards
            if (pattern.Contains('*'))
            {
                // Use glob matching for wildcard patterns
                try
                {
                    // Check root
                    if (Directory.EnumerateFiles(root, pattern).Any())
                        return true;
                    
                    // Check immediate subdirectories
                    foreach (var dir in Directory.EnumerateDirectories(root))
                    {
                        if (ExcludedFolders.Contains(Path.GetFileName(dir)))
                            continue;
                        
                        if (Directory.EnumerateFiles(dir, pattern).Any())
                            return true;
                    }
                }
                catch (UnauthorizedAccessException) { }
            }
            else
            {
                // Exact file name - use File.Exists for efficiency
                if (File.Exists(Path.Combine(root, pattern)))
                    return true;
                
                // Check immediate subdirectories
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(root))
                    {
                        if (ExcludedFolders.Contains(Path.GetFileName(dir)))
                            continue;
                        
                        if (File.Exists(Path.Combine(dir, pattern)))
                            return true;
                    }
                }
                catch (UnauthorizedAccessException) { }
            }
        }
        return false;
    }
    
    private static bool HasAnySourceFiles(string folder)
    {
        var commonExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".py", ".java", ".ts", ".js", ".go"
        };
        
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.*", MediumOptions))
            {
                if (commonExtensions.Contains(Path.GetExtension(file)))
                    return true;
            }
        }
        catch (UnauthorizedAccessException) { }
        
        return false;
    }
    
    private static bool HasSourceFilesShallow(string folder)
    {
        var commonExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".py", ".java", ".ts", ".js", ".go"
        };
        
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                if (commonExtensions.Contains(Path.GetExtension(file)))
                    return true;
            }
        }
        catch (UnauthorizedAccessException) { }
        
        return false;
    }
    
    /// <summary>
    /// Counts source files with the given extension in the folder.
    /// For root-level comparison (flat vs nested), counts shallow first.
    /// If no shallow files, counts recursively with depth limit.
    /// </summary>
    private static int CountSourceFiles(string folder, string extension)
    {
        try
        {
            // First count shallow (top-level files)
            var shallowCount = Directory.EnumerateFiles(folder, "*" + extension, SearchOption.TopDirectoryOnly).Count();
            if (shallowCount > 0)
                return shallowCount;
            
            // If no top-level files, count recursively (but cap at 100 for performance)
            var count = 0;
            foreach (var _ in Directory.EnumerateFiles(folder, "*" + extension, SearchOption.AllDirectories))
            {
                if (++count >= 100)
                    break;
            }
            return count;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }
    
    private static int CountFilesQuick(string folder)
    {
        try
        {
            var count = 0;
            foreach (var _ in Directory.EnumerateFiles(folder, "*.*", MediumOptions))
            {
                if (++count >= 10)
                    break;
            }
            return count;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }
    
    private record LanguagePattern(
        SdkLanguage LanguageEnum,
        string Name,
        string FileExtension,
        string[] BuildFilePatterns,
        string[] SourceFolderPatterns
    );
}
