// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// All source file extensions recognized by the detection system.
    /// Used as a fallback when the language is not yet known.
    /// </summary>
    private static readonly HashSet<string> AllSourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".py", ".java", ".ts", ".js", ".go"
    };

    // Samples folder candidates in priority order (searched at root and one level deep)
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

    /// <summary>
    /// Language-specific detection patterns.
    /// <para>
    /// <b>Ordering contract:</b> Patterns are evaluated in declaration order. The first match wins.
    /// More specific languages (those with unique build markers) come first:
    /// 1. .NET (*.csproj, *.sln) — unique markers
    /// 2. Python (setup.py, pyproject.toml) — unique markers
    /// 3. Java (pom.xml, build.gradle) — unique markers
    /// 4. TypeScript (tsconfig.json) — unique marker; must precede JavaScript
    /// 5. JavaScript (package.json) — broad marker; handled last among web languages
    ///    because package.json is often present for tooling (eslint, prettier) in non-JS repos
    /// 6. Go (go.mod) — unique marker
    /// </para>
    /// <para>
    /// The conventional default samples folder name for each language is also specified.
    /// </para>
    /// </summary>
    private static readonly LanguagePattern[] LanguagePatterns =
    [
        // .NET — unique markers (*.csproj, *.sln)
        new(SdkLanguage.DotNet, "dotnet", ".cs",
            new[] { "*.csproj", "*.sln" },
            new[] { "src", "lib", "source" },
            "examples"),
        
        // Python — unique markers (setup.py, pyproject.toml)
        new(SdkLanguage.Python, "python", ".py",
            new[] { "setup.py", "pyproject.toml" },
            new[] { "src", "lib", "." },
            "examples"),
        
        // Java — unique markers (pom.xml, build.gradle)
        new(SdkLanguage.Java, "java", ".java",
            new[] { "pom.xml", "build.gradle", "build.gradle.kts" },
            new[] { "src/main/java", "src", "*/src/main/java", "*/src" },
            "examples"),
        
        // TypeScript — must precede JavaScript (tsconfig.json is unique)
        new(SdkLanguage.TypeScript, "typescript", ".ts",
            new[] { "tsconfig.json" },
            new[] { "src", "lib" },
            "examples"),
        
        // JavaScript — package.json is a broad marker; many non-JS repos have one for tooling.
        // Placed after TypeScript to avoid misclassification when tsconfig.json is present.
        new(SdkLanguage.JavaScript, "javascript", ".js",
            new[] { "package.json" },
            new[] { "src", "lib" },
            "examples"),
        
        // Go — unique marker (go.mod)
        new(SdkLanguage.Go, "go", ".go",
            new[] { "go.mod" },
            new[] { "pkg", "internal", "cmd", "." },
            "examples")
    ];

    /// <summary>
    /// Folders to exclude from source and samples enumeration.
    /// These are typically build artifacts, dependencies, or version control folders
    /// that should never be scanned (e.g., node_modules can contain 100K+ files).
    /// </summary>
    /// <remarks>Delegates to <see cref="SafeFileEnumerator.ExcludedFolders"/>.</remarks>
    public static IReadOnlySet<string> ExcludedFolders => SafeFileEnumerator.ExcludedFolders;

    /// <summary>
    /// Safe enumeration options that skip excluded folders.
    /// Use this when enumerating files to avoid scanning node_modules, .git, etc.
    /// </summary>
    /// <remarks>Delegates to <see cref="SafeFileEnumerator.SafeEnumerationOptions"/>.</remarks>
    public static EnumerationOptions SafeEnumerationOptions => SafeFileEnumerator.SafeEnumerationOptions;

    /// <summary>
    /// Safely enumerates files in a directory, skipping excluded folders like node_modules.
    /// This is the preferred method for file enumeration to avoid performance issues.
    /// </summary>
    /// <remarks>Delegates to <see cref="SafeFileEnumerator.EnumerateFiles"/>.</remarks>
    public static IEnumerable<string> EnumerateFilesSafely(
        string directory,
        string searchPattern = "*.*",
        int maxFiles = 10000) => SafeFileEnumerator.EnumerateFiles(directory, searchPattern, maxFiles);

    /// <summary>
    /// Safely counts files matching a pattern, skipping excluded folders.
    /// </summary>
    /// <remarks>Delegates to <see cref="SafeFileEnumerator.CountFiles"/>.</remarks>
    public static int CountFilesSafely(string directory, string searchPattern = "*.*", int maxCount = 10000)
        => SafeFileEnumerator.CountFiles(directory, searchPattern, maxCount);

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

    /// <summary>
    /// The library/package name extracted from build markers (e.g., "azure-storage-blob"
    /// from pyproject.toml, "@azure/openai" from package.json). Null if not determined.
    /// </summary>
    public string? LibraryName { get; }

    /// <summary>Whether the SDK was successfully detected.</summary>
    public bool IsValid => Language != null || SourceFolder != RootPath;

    private SdkInfo(
        string rootPath,
        SdkLanguage? language,
        string? languageName,
        string? fileExtension,
        string sourceFolder,
        string? samplesFolder,
        string defaultSamplesFolderName,
        List<string> allSamplesCandidates,
        string? libraryName)
    {
        RootPath = rootPath;
        SdkName = Path.GetFileName(rootPath);
        Language = language;
        LanguageName = languageName;
        FileExtension = fileExtension;
        SourceFolder = sourceFolder;
        SamplesFolder = samplesFolder;
        SuggestedSamplesFolder = samplesFolder ?? Path.Combine(rootPath, defaultSamplesFolderName);
        AllSamplesCandidates = allSamplesCandidates.AsReadOnly();
        LibraryName = libraryName;
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
    /// <remarks>
    /// The cache is keyed by path and never automatically invalidates on filesystem changes.
    /// If files are added/removed after scanning (e.g., via <c>git checkout</c>), stale results
    /// will be returned until this method is called. Callers that need fresh results after
    /// filesystem mutations should call <see cref="ClearCache"/> first.
    /// </remarks>
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
                var resolved = ResolveLanguagePattern(sdkRoot, pattern);
                return resolved.LanguageEnum != SdkLanguage.Unknown ? resolved.LanguageEnum : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the actual language pattern, handling disambiguation (e.g., TypeScript vs JavaScript).
    /// When a package.json is found, checks for tsconfig.json to determine if it's TypeScript.
    /// Also validates that package.json represents a library (has "main", "exports", "module", or "types").
    /// </summary>
    private static LanguagePattern ResolveLanguagePattern(string root, LanguagePattern pattern)
    {
        if (pattern.LanguageEnum == SdkLanguage.JavaScript)
        {
            // Check for tsconfig.json at root or one level deep
            if (HasTsConfig(root))
            {
                return LanguagePatterns.First(p => p.LanguageEnum == SdkLanguage.TypeScript);
            }

            // Validate package.json is a library, not just tooling config
            if (!IsLibraryPackageJson(root))
            {
                // Return a sentinel pattern that won't match — caller should skip this
                return new LanguagePattern(SdkLanguage.Unknown, "unknown", ".js", [], [], "examples");
            }
        }

        return pattern;
    }

    /// <summary>
    /// Checks for tsconfig.json at root or in immediate subdirectories.
    /// </summary>
    private static bool HasTsConfig(string root)
    {
        if (File.Exists(Path.Combine(root, "tsconfig.json")))
            return true;

        // Check immediate subdirectories (for monorepo patterns)
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (ExcludedFolders.Contains(Path.GetFileName(dir)))
                    continue;
                if (File.Exists(Path.Combine(dir, "tsconfig.json")))
                    return true;
            }
        }
        catch (UnauthorizedAccessException) { }

        return false;
    }

    /// <summary>
    /// Validates that a package.json at <paramref name="root"/> represents a library
    /// or JS/TS project, not purely a tooling configuration file.
    /// <para>
    /// Returns false only when the package.json is explicitly marked as private AND lacks
    /// any library-export fields ("main", "exports", "module", "types", "typings").
    /// This conservatively avoids rejecting legitimate JS projects that simply don't
    /// declare "main" (e.g., those using module.exports in source files).
    /// </para>
    /// </summary>
    private static bool IsLibraryPackageJson(string root)
    {
        var packageJsonPath = Path.Combine(root, "package.json");
        if (!File.Exists(packageJsonPath))
            return true; // No package.json to validate

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(packageJsonPath));
            var rootObj = doc.RootElement;

            // Only reject if explicitly private with no library exports
            var isPrivate = rootObj.TryGetProperty("private", out var privateProp) &&
                            privateProp.ValueKind == JsonValueKind.True;

            if (!isPrivate)
                return true; // Not private — treat as library

            // Private package — check for library exports
            return rootObj.TryGetProperty("main", out _) ||
                   rootObj.TryGetProperty("exports", out _) ||
                   rootObj.TryGetProperty("module", out _) ||
                   rootObj.TryGetProperty("types", out _) ||
                   rootObj.TryGetProperty("typings", out _);
        }
        catch
        {
            return true; // Parse failure — benefit of the doubt
        }
    }

    private static SdkInfo ScanInternal(string root)
    {
        using var activity = Telemetry.SdkChatTelemetry.StartScan(root);
        try
        {
            // Detect language and source folder
            var (sourceFolder, languageEnum, languageName, fileExt, detectedPattern) = DetectSourceFolder(root);

            // Extract library name for import-based samples detection
            string? libraryName = languageEnum.HasValue
                ? ExtractLibraryName(root, languageEnum.Value)
                : null;

            // Detect samples folders, scoring by import count when library name is known,
            // falling back to source-file count when it's not
            var (samplesFolder, allCandidates) = DetectSamplesFolder(root, fileExt, libraryName, languageEnum);

            // Determine language-aware default samples folder name
            var defaultSamplesName = detectedPattern?.DefaultSamplesFolderName ?? "examples";

            var result = new SdkInfo(
                rootPath: root,
                language: languageEnum,
                languageName: languageName,
                fileExtension: fileExt,
                sourceFolder: sourceFolder,
                samplesFolder: samplesFolder,
                defaultSamplesFolderName: defaultSamplesName,
                allSamplesCandidates: allCandidates,
                libraryName: libraryName
            );

            activity?.SetTag("sdk.language", languageName ?? "unknown");
            activity?.SetTag("sdk.has_samples", samplesFolder != null);
            activity?.SetTag("sdk.source_folder", sourceFolder);
            activity?.SetTag("sdk.samples_folder", samplesFolder ?? "(none)");
            activity?.SetTag("sdk.samples_candidates", allCandidates.Count);
            activity?.SetTag("sdk.library_name", libraryName ?? "(unknown)");

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
                defaultSamplesFolderName: "examples",
                allSamplesCandidates: [],
                libraryName: null
            );
        }
    }

    private static (string SourceFolder, SdkLanguage? Language, string? LanguageName, string? FileExt, LanguagePattern? Pattern) DetectSourceFolder(string root)
    {
        // Try each language pattern in priority order (see LanguagePatterns doc for ordering contract)
        foreach (var pattern in LanguagePatterns)
        {
            if (!HasBuildMarker(root, pattern.BuildFilePatterns))
                continue;

            // Resolve language disambiguation (e.g., TypeScript vs JavaScript)
            var actualPattern = ResolveLanguagePattern(root, pattern);

            // If disambiguation returned Unknown (e.g., package.json without library markers),
            // skip this pattern and try the next one
            if (actualPattern.LanguageEnum == SdkLanguage.Unknown)
                continue;

            // Find best source folder by counting source files using SafeFileEnumerator.
            // All candidates are scored consistently using recursive counts that skip excluded folders.
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

            // Only return a match if we actually found source files (issue #6)
            if (bestCandidate != null && bestCount > 0)
            {
                var langEnum = actualPattern.LanguageEnum != SdkLanguage.Unknown
                    ? actualPattern.LanguageEnum
                    : (SdkLanguage?)null;
                return (bestCandidate, langEnum, actualPattern.Name, actualPattern.FileExtension, actualPattern);
            }
        }

        // Fallback: look for any common source folder with any source files
        var fallbackFolders = new[] { "src", "lib", "source", "sdk", "pkg" };
        foreach (var folder in fallbackFolders)
        {
            var candidate = Path.Combine(root, folder);
            if (Directory.Exists(candidate) && HasAnySourceFiles(candidate))
            {
                return (candidate, null, null, null, null);
            }
        }

        // Last resort: use root if it has source files
        if (HasSourceFilesShallow(root))
        {
            return (root, null, null, null, null);
        }

        return (root, null, null, null, null);
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

    /// <summary>
    /// Detects samples folders using a two-phase approach:
    /// <list type="number">
    /// <item><b>Convention-based:</b> searches for folders matching <see cref="SamplesFolderPatterns"/>.</item>
    /// <item><b>Import-based (when library name is available):</b> scans all non-excluded top-level
    /// directories for source files that import the SDK library, discovering unconventionally-named
    /// samples folders (e.g., "quickstart", "tutorials", "getting-started").</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Scoring: when a library name is available, candidates are ranked primarily by the number of
    /// files that import the library (strongest signal), with source-file count as a tiebreaker.
    /// When no library name is available, falls back to pure source-file-count scoring.
    /// </remarks>
    private static (string? SamplesFolder, List<string> AllCandidates) DetectSamplesFolder(
        string root, string? languageExtension, string? libraryName, SdkLanguage? language)
    {
        // Build import pattern if library name is available
        Regex? importPattern = null;
        if (!string.IsNullOrEmpty(libraryName) && language.HasValue)
        {
            importPattern = BuildImportPattern(libraryName, language.Value);
        }

        List<(string Path, int ImportScore, int FileScore)> candidates = [];

        // Phase 1: Convention-based candidates from known folder name patterns
        foreach (var pattern in SamplesFolderPatterns)
        {
            // Check if pattern contains wildcards
            if (pattern.Contains('*'))
            {
                // Search root level
                SearchGlobSamplesCandidates(root, pattern, languageExtension, importPattern, candidates);

                // Also search one level deep for monorepo patterns (e.g., sdk/*-examples)
                try
                {
                    foreach (var subdir in Directory.EnumerateDirectories(root))
                    {
                        if (ExcludedFolders.Contains(Path.GetFileName(subdir)))
                            continue;
                        SearchGlobSamplesCandidates(subdir, pattern, languageExtension, importPattern, candidates);
                    }
                }
                catch (UnauthorizedAccessException) { }
            }
            else
            {
                var candidate = Path.Combine(root, pattern);
                if (!Directory.Exists(candidate))
                    continue;

                var fileCount = CountSamplesSourceFiles(candidate, languageExtension);
                if (fileCount > 0)
                {
                    var importCount = importPattern != null
                        ? CountImportingFiles(candidate, importPattern, languageExtension)
                        : 0;
                    candidates.Add((candidate, importCount, fileCount));
                }
            }
        }

        // Phase 2: Import-based discovery — scan all non-excluded top-level directories
        // for files that import the library, regardless of folder naming conventions.
        // This discovers unconventionally-named samples like "quickstart/", "tutorials/", etc.
        if (importPattern != null)
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    var name = Path.GetFileName(dir);

                    // Skip excluded folders (node_modules, .git, etc.)
                    if (ExcludedFolders.Contains(name))
                        continue;

                    // Skip if already discovered by convention matching
                    if (candidates.Exists(c => string.Equals(c.Path, dir, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    // Skip common source/build/test folders that shouldn't be samples
                    if (IsSourceOrBuildFolder(name))
                        continue;

                    var importCount = CountImportingFiles(dir, importPattern, languageExtension);
                    if (importCount > 0)
                    {
                        var fileCount = CountSamplesSourceFiles(dir, languageExtension);
                        candidates.Add((dir, importCount, fileCount));
                    }
                }
            }
            catch (UnauthorizedAccessException) { }
        }

        // Phase 3: Sort candidates
        if (importPattern != null)
        {
            // Primary: import count descending, Secondary: file count descending
            candidates.Sort((a, b) =>
            {
                var cmp = b.ImportScore.CompareTo(a.ImportScore);
                return cmp != 0 ? cmp : b.FileScore.CompareTo(a.FileScore);
            });
        }
        else
        {
            // Fallback: file count descending (existing behavior when no library name)
            candidates.Sort((a, b) => b.FileScore.CompareTo(a.FileScore));
        }

        var allPaths = candidates.ConvertAll(c => c.Path);
        var bestMatch = candidates.Count > 0 ? candidates[0].Path : null;

        return (bestMatch, allPaths);
    }

    /// <summary>
    /// Folders that are clearly source/build/test infrastructure and should not be
    /// considered as samples candidates during import-based discovery.
    /// </summary>
    private static bool IsSourceOrBuildFolder(string folderName)
    {
        return folderName is "src" or "lib" or "source" or "sdk" or "pkg" or "internal" or "cmd"
            or "build" or "dist" or "out" or "target" or "output" or "publish"
            or "test" or "tests" or "spec" or "specs" or "__tests__"
            or "docs" or "doc" or "documentation"
            or ".github" or ".vscode" or ".idea" or ".vs"
            or "scripts" or "tools" or "ci" or ".ci"
            or "generated" or "auto-generated";
    }

    /// <summary>
    /// Searches for directories matching a glob pattern and adds them as samples candidates.
    /// </summary>
    private static void SearchGlobSamplesCandidates(
        string searchDir, string pattern, string? languageExtension, Regex? importPattern,
        List<(string Path, int ImportScore, int FileScore)> candidates)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(searchDir, pattern))
            {
                // Skip if already found
                if (candidates.Exists(c => string.Equals(c.Path, dir, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var fileCount = CountSamplesSourceFiles(dir, languageExtension);
                if (fileCount > 0)
                {
                    var importCount = importPattern != null
                        ? CountImportingFiles(dir, importPattern, languageExtension)
                        : 0;
                    candidates.Add((dir, importCount, fileCount));
                }
            }
        }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Counts source files in a samples folder.
    /// If <paramref name="languageExtension"/> is provided, counts only files of that type.
    /// Otherwise counts all known source file types (.cs, .py, .java, .ts, .js, .go).
    /// Uses SafeFileEnumerator with a cap of 200 for accurate ranking.
    /// </summary>
    private static int CountSamplesSourceFiles(string folder, string? languageExtension)
    {
        if (!string.IsNullOrEmpty(languageExtension))
        {
            return SafeFileEnumerator.CountFiles(folder, "*" + languageExtension, maxCount: 200);
        }

        // Count all known source file types
        var total = 0;
        foreach (var ext in AllSourceExtensions)
        {
            total += SafeFileEnumerator.CountFiles(folder, "*" + ext, maxCount: 200);
            if (total >= 200)
                return total;
        }
        return total;
    }

    /// <summary>
    /// Checks whether any of the given build-file patterns exist in the specified directory.
    /// <para>
    /// <b>Search contract:</b> Checks the root directory AND all immediate (depth-1) subdirectories,
    /// skipping directories in <see cref="ExcludedFolders"/>. This enables detection of multi-module
    /// repos where the build marker is in a submodule (e.g., my-sdk/sub-module/pom.xml triggers
    /// Java detection for my-sdk/).
    /// </para>
    /// <para>
    /// The subdirectory list is enumerated once and reused across all patterns for efficiency.
    /// </para>
    /// </summary>
    private static bool HasBuildMarker(string root, string[] patterns)
    {
        // Enumerate subdirectories once and reuse across all patterns
        string[]? subdirs = null;

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
                    subdirs ??= GetNonExcludedSubdirectories(root);
                    foreach (var dir in subdirs)
                    {
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
                subdirs ??= GetNonExcludedSubdirectories(root);
                foreach (var dir in subdirs)
                {
                    if (File.Exists(Path.Combine(dir, pattern)))
                        return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Returns all immediate subdirectories of the given root, excluding folders in
    /// <see cref="ExcludedFolders"/>. Returns an empty array on access errors.
    /// </summary>
    private static string[] GetNonExcludedSubdirectories(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root)
                .Where(d => !ExcludedFolders.Contains(Path.GetFileName(d)))
                .ToArray();
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Checks if a folder contains any source files (any language) recursively,
    /// using SafeFileEnumerator to skip excluded folders.
    /// </summary>
    private static bool HasAnySourceFiles(string folder)
    {
        foreach (var ext in AllSourceExtensions)
        {
            if (SafeFileEnumerator.EnumerateFiles(folder, "*" + ext, maxFiles: 1).Any())
                return true;
        }
        return false;
    }

    /// <summary>
    /// Checks if a folder has any source files at the top level only (no recursion).
    /// </summary>
    private static bool HasSourceFilesShallow(string folder)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                if (AllSourceExtensions.Contains(Path.GetExtension(file)))
                    return true;
            }
        }
        catch (UnauthorizedAccessException) { }

        return false;
    }

    /// <summary>
    /// Counts source files with the given extension in the folder.
    /// Uses SafeFileEnumerator to consistently skip excluded folders (node_modules, bin, etc.)
    /// and count recursively with a cap of 200 for performance.
    /// All candidates are scored on the same scale (total recursive count, not shallow-first).
    /// </summary>
    private static int CountSourceFiles(string folder, string extension)
    {
        return SafeFileEnumerator.CountFiles(folder, "*" + extension, maxCount: 200);
    }

    /// <summary>
    /// Maximum number of lines to read per file when scanning for import statements.
    /// Import declarations appear near the top of source files in all supported languages.
    /// </summary>
    private const int MaxImportScanLines = 50;

    /// <summary>
    /// Maximum number of files to scan per candidate directory when checking for imports.
    /// Keeps the import-based detection bounded for large directories.
    /// </summary>
    private const int MaxImportScanFiles = 100;

    /// <summary>
    /// Extracts the library/package name from language-specific build markers.
    /// Returns null if the name cannot be determined.
    /// </summary>
    /// <remarks>
    /// Per-language extraction:
    /// <list type="bullet">
    /// <item><b>Python:</b> <c>pyproject.toml</c> → <c>[project] name = "..."</c></item>
    /// <item><b>JS/TS:</b> <c>package.json</c> → <c>"name": "..."</c></item>
    /// <item><b>Java:</b> <c>pom.xml</c> → <c>&lt;groupId&gt;</c> (first unique segments)</item>
    /// <item><b>Go:</b> <c>go.mod</c> → <c>module</c> path</item>
    /// <item><b>.NET:</b> <c>*.csproj</c> → <c>&lt;RootNamespace&gt;</c> or project file name</item>
    /// </list>
    /// </remarks>
    internal static string? ExtractLibraryName(string root, SdkLanguage language)
    {
        try
        {
            return language switch
            {
                SdkLanguage.Python => ExtractPythonPackageName(root),
                SdkLanguage.JavaScript or SdkLanguage.TypeScript => ExtractNpmPackageName(root),
                SdkLanguage.Java => ExtractJavaGroupId(root),
                SdkLanguage.Go => ExtractGoModulePath(root),
                SdkLanguage.DotNet => ExtractDotNetNamespace(root),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts the Python package name from pyproject.toml.
    /// Parses <c>[project] name = "azure-storage-blob"</c> and transforms hyphens
    /// to underscores for the canonical Python import name.
    /// </summary>
    private static string? ExtractPythonPackageName(string root)
    {
        var pyprojectPath = Path.Combine(root, "pyproject.toml");
        if (!File.Exists(pyprojectPath))
            return null;

        // Simple TOML parsing: look for name = "..." in [project] section
        var inProjectSection = false;
        foreach (var line in File.ReadLines(pyprojectPath))
        {
            var trimmed = line.TrimStart();

            // Track section headers
            if (trimmed.StartsWith('['))
            {
                inProjectSection = trimmed.StartsWith("[project]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inProjectSection)
                continue;

            // Match name = "value" or name = 'value'
            var match = Regex.Match(trimmed, @"^name\s*=\s*[""']([^""']+)[""']");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts the npm package name from package.json → <c>"name"</c> field.
    /// </summary>
    private static string? ExtractNpmPackageName(string root)
    {
        var packageJsonPath = Path.Combine(root, "package.json");
        if (!File.Exists(packageJsonPath))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(packageJsonPath));
            if (doc.RootElement.TryGetProperty("name", out var nameProp) &&
                nameProp.ValueKind == JsonValueKind.String)
            {
                var name = nameProp.GetString();
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Extracts the Java group/package prefix from pom.xml → <c>&lt;groupId&gt;</c>,
    /// or from build.gradle → <c>group = '...'</c>.
    /// </summary>
    private static string? ExtractJavaGroupId(string root)
    {
        // Try pom.xml first
        var pomPath = Path.Combine(root, "pom.xml");
        if (File.Exists(pomPath))
        {
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(pomPath);
                var ns = doc.Root?.GetDefaultNamespace() ?? System.Xml.Linq.XNamespace.None;
                var groupId = doc.Root?.Element(ns + "groupId")?.Value;
                if (!string.IsNullOrWhiteSpace(groupId))
                    return groupId;
            }
            catch { }
        }

        // Try build.gradle / build.gradle.kts
        var gradlePaths = new[] { "build.gradle", "build.gradle.kts" };
        foreach (var gradleFile in gradlePaths)
        {
            var gradlePath = Path.Combine(root, gradleFile);
            if (!File.Exists(gradlePath))
                continue;

            try
            {
                foreach (var line in File.ReadLines(gradlePath))
                {
                    // Match: group = 'com.azure' or group = "com.azure" or group 'com.azure'
                    var match = Regex.Match(line, @"group\s*=?\s*[""']([^""']+)[""']");
                    if (match.Success)
                        return match.Groups[1].Value;
                }
            }
            catch { }
        }

        return null;
    }

    /// <summary>
    /// Extracts the Go module path from go.mod → <c>module github.com/org/repo/...</c>.
    /// </summary>
    private static string? ExtractGoModulePath(string root)
    {
        var goModPath = Path.Combine(root, "go.mod");
        if (!File.Exists(goModPath))
            return null;

        try
        {
            foreach (var line in File.ReadLines(goModPath))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("module ", StringComparison.Ordinal))
                {
                    var modulePath = trimmed["module ".Length..].Trim();
                    return string.IsNullOrWhiteSpace(modulePath) ? null : modulePath;
                }
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Extracts the .NET root namespace from the first <c>*.csproj</c> found at root or in src/.
    /// Falls back to deriving the namespace from the project file name (e.g., <c>Azure.Storage.Blobs</c>).
    /// </summary>
    private static string? ExtractDotNetNamespace(string root)
    {
        // Find the first .csproj
        string? csprojPath = null;
        try
        {
            // Check root
            csprojPath = Directory.EnumerateFiles(root, "*.csproj").FirstOrDefault();

            // Check src/
            if (csprojPath == null)
            {
                var srcDir = Path.Combine(root, "src");
                if (Directory.Exists(srcDir))
                {
                    csprojPath = Directory.EnumerateFiles(srcDir, "*.csproj").FirstOrDefault();
                    // Also check immediate subdirectories of src/
                    if (csprojPath == null)
                    {
                        foreach (var dir in Directory.EnumerateDirectories(srcDir))
                        {
                            csprojPath = Directory.EnumerateFiles(dir, "*.csproj").FirstOrDefault();
                            if (csprojPath != null)
                                break;
                        }
                    }
                }
            }
        }
        catch { }

        if (csprojPath == null)
            return null;

        // Try to extract RootNamespace from csproj XML
        try
        {
            var doc = System.Xml.Linq.XDocument.Load(csprojPath);
            var ns = doc.Root?.GetDefaultNamespace() ?? System.Xml.Linq.XNamespace.None;
            var rootNs = doc.Root?.Descendants(ns + "RootNamespace").FirstOrDefault()?.Value;
            if (!string.IsNullOrWhiteSpace(rootNs))
                return rootNs;
        }
        catch { }

        // Fallback: derive from project file name (e.g., "Azure.Storage.Blobs.csproj" → "Azure.Storage.Blobs")
        return Path.GetFileNameWithoutExtension(csprojPath);
    }

    /// <summary>
    /// Builds a compiled <see cref="Regex"/> that matches import statements referencing
    /// the given library name for the specified language.
    /// Returns null if the library name or language doesn't support pattern building.
    /// </summary>
    /// <remarks>
    /// Per-language patterns:
    /// <list type="bullet">
    /// <item><b>Python:</b> <c>import name</c> / <c>from name import</c> (hyphens → underscores and dots)</item>
    /// <item><b>JS/TS:</b> <c>from 'name'</c> / <c>require('name')</c></item>
    /// <item><b>Java:</b> <c>import groupId.*</c></item>
    /// <item><b>Go:</b> <c>"module/path"</c> inside import blocks</item>
    /// <item><b>.NET:</b> <c>using Namespace;</c></item>
    /// </list>
    /// </remarks>
    internal static Regex? BuildImportPattern(string libraryName, SdkLanguage language)
    {
        if (string.IsNullOrWhiteSpace(libraryName))
            return null;

        string pattern;

        switch (language)
        {
            case SdkLanguage.Python:
            {
                // Transform: "azure-storage-blob" → search for "azure_storage_blob" and "azure.storage.blob"
                var underscored = Regex.Escape(libraryName.Replace('-', '_'));
                var dotted = Regex.Escape(libraryName.Replace('-', '.'));

                // Match: import <name>, from <name> import, from <name>.<sub> import
                if (underscored == dotted)
                {
                    pattern = $@"(?:^|\s)(?:import|from)\s+{underscored}\b";
                }
                else
                {
                    pattern = $@"(?:^|\s)(?:import|from)\s+(?:{underscored}|{dotted})\b";
                }
                break;
            }

            case SdkLanguage.JavaScript:
            case SdkLanguage.TypeScript:
            {
                // Match: from '<name>'/from "<name>", require('<name>')/require("<name>")
                // Also match sub-path imports: from '<name>/sub'
                var escaped = Regex.Escape(libraryName);
                pattern = $@"(?:from|require\s*\()\s*['""](?:\.\.?/)*{escaped}(?:/[^'""]*)?['""]";
                break;
            }

            case SdkLanguage.Java:
            {
                // groupId like "com.azure" → match "import com.azure."
                var escaped = Regex.Escape(libraryName);
                pattern = $@"import\s+{escaped}\.";
                break;
            }

            case SdkLanguage.Go:
            {
                // Module path → match "github.com/org/repo" in import statements
                var escaped = Regex.Escape(libraryName);
                pattern = $@"[""]{escaped}(?:/[^""]*)?[""]\s*$";
                break;
            }

            case SdkLanguage.DotNet:
            {
                // Namespace → match "using Namespace;" or "using Namespace."
                var escaped = Regex.Escape(libraryName);
                pattern = $@"using\s+(?:static\s+)?{escaped}[.;\s]";
                break;
            }

            default:
                return null;
        }

        try
        {
            return new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Counts files in a directory whose first <see cref="MaxImportScanLines"/> lines
    /// contain an import statement matching the given pattern.
    /// Uses <see cref="SafeFileEnumerator"/> to stay within bounded enumeration constraints.
    /// </summary>
    internal static int CountImportingFiles(string folder, Regex importPattern, string? fileExtension)
    {
        if (string.IsNullOrEmpty(fileExtension))
            return 0;

        var count = 0;
        var searchPattern = "*" + fileExtension;

        foreach (var filePath in SafeFileEnumerator.EnumerateFiles(folder, searchPattern, MaxImportScanFiles))
        {
            if (FileContainsImport(filePath, importPattern))
                count++;
        }

        return count;
    }

    /// <summary>
    /// Reads the first <see cref="MaxImportScanLines"/> lines of a file and returns
    /// true if any line matches the import pattern.
    /// </summary>
    private static bool FileContainsImport(string filePath, Regex importPattern)
    {
        try
        {
            var linesRead = 0;
            foreach (var line in File.ReadLines(filePath))
            {
                if (++linesRead > MaxImportScanLines)
                    break;

                if (importPattern.IsMatch(line))
                    return true;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return false;
    }

    private sealed record LanguagePattern(
        SdkLanguage LanguageEnum,
        string Name,
        string FileExtension,
        string[] BuildFilePatterns,
        string[] SourceFolderPatterns,
        string DefaultSamplesFolderName
    );
}
