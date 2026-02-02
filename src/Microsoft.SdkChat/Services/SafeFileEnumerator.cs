// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.SdkChat.Services;

/// <summary>
/// Safe file system operations that skip dangerous folders.
/// Extracted from SdkInfo for testability and single responsibility.
/// </summary>
public static class SafeFileEnumerator
{
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
    public static IEnumerable<string> EnumerateFiles(
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
    public static int CountFiles(string directory, string searchPattern = "*.*", int maxCount = 10000)
    {
        return EnumerateFiles(directory, searchPattern, maxCount).Count();
    }

    /// <summary>
    /// Checks if a directory name is in the excluded list.
    /// </summary>
    public static bool IsExcluded(string directoryName)
    {
        return ExcludedFolders.Contains(directoryName);
    }
}
