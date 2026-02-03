// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using Xunit;

namespace Microsoft.SdkChat.IntegrationTests;

/// <summary>
/// Collection for release container integration tests.
/// Tests can run in parallel since each invokes the wrapper script independently.
/// </summary>
[CollectionDefinition("ReleaseContainer")]
public class ReleaseContainerCollection : ICollectionFixture<ReleaseContainerFixture>
{
}

/// <summary>
/// Fixture for running integration tests using the release container via wrapper script.
/// Uses scripts/sdk-chat.sh which handles image building, path mounting, and credentials.
/// </summary>
public class ReleaseContainerFixture : IAsyncLifetime
{
    private static readonly string RepoRoot = FindRepoRoot();
    
    /// <summary>
    /// Path to the wrapper script that handles all Docker setup.
    /// </summary>
    public string WrapperScript => Path.Combine(RepoRoot, "scripts", "sdk-chat.sh");
    
    /// <summary>
    /// Path to test fixtures (shared with ApiExtractor.Tests).
    /// </summary>
    public string FixturesPath => Path.Combine(RepoRoot, "tests", "ApiExtractor.Tests", "TestFixtures");
    
    /// <summary>
    /// Whether the test environment is available (Docker + script exists).
    /// </summary>
    public bool IsAvailable { get; private set; }
    
    /// <summary>
    /// Reason why tests are being skipped, if any.
    /// </summary>
    public string? SkipReason { get; private set; }

    public async Task InitializeAsync()
    {
        // Check wrapper script exists
        if (!File.Exists(WrapperScript))
        {
            SkipReason = $"Wrapper script not found: {WrapperScript}";
            IsAvailable = false;
            return;
        }

        // Check Docker is available
        var dockerCheck = await RunProcessAsync("docker", ["info"], timeoutSeconds: 10);
        if (dockerCheck.ExitCode != 0)
        {
            SkipReason = "Docker not available";
            IsAvailable = false;
            return;
        }

        // Check release image exists (or suggest building)
        var imageCheck = await RunProcessAsync("docker", ["image", "inspect", "sdk-chat:latest"], timeoutSeconds: 10);
        if (imageCheck.ExitCode != 0)
        {
            SkipReason = "Release image not found. Run: ./scripts/sdk-chat.sh --build --help";
            IsAvailable = false;
            return;
        }

        // Verify fixtures exist
        if (!Directory.Exists(FixturesPath))
        {
            SkipReason = $"Test fixtures not found: {FixturesPath}";
            IsAvailable = false;
            return;
        }

        IsAvailable = true;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Runs a command using the wrapper script, which handles Docker setup.
    /// </summary>
    /// <param name="args">Arguments to pass to sdk-chat CLI.</param>
    /// <param name="timeoutSeconds">Timeout in seconds.</param>
    /// <returns>Exit code, stdout, and stderr.</returns>
    public async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string[] args,
        int timeoutSeconds = 120)
    {
        // Use bash to run the wrapper script
        var scriptArgs = new List<string> { WrapperScript };
        scriptArgs.AddRange(args);
        
        return await RunProcessAsync("bash", [.. scriptArgs], timeoutSeconds);
    }

    /// <summary>
    /// Runs a command with a fixture path for a specific language.
    /// </summary>
    /// <param name="command">Base command (e.g., "package api extract").</param>
    /// <param name="language">Fixture language folder (DotNet, Python, etc.).</param>
    /// <param name="additionalArgs">Additional CLI args (e.g., "--language dotnet").</param>
    /// <param name="timeoutSeconds">Timeout in seconds.</param>
    public async Task<(int ExitCode, string Output, string Error)> RunWithFixtureAsync(
        string command,
        string language,
        string? additionalArgs = null,
        int timeoutSeconds = 120)
    {
        var fixturePath = Path.Combine(FixturesPath, language);
        
        // Parse command into args, append fixture path, then additional args
        var args = command.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        args.Add(fixturePath);
        
        if (!string.IsNullOrEmpty(additionalArgs))
        {
            args.AddRange(additionalArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
        
        return await RunAsync([.. args], timeoutSeconds);
    }

    /// <summary>
    /// Maps fixture folder names to CLI --language flag values.
    /// </summary>
    public static string GetLanguageFlag(string fixtureLanguage) => fixtureLanguage switch
    {
        "DotNet" => "dotnet",
        "Python" => "python",
        "Go" => "go",
        "Java" => "java",
        "TypeScript" => "typescript",
        _ => fixtureLanguage.ToLowerInvariant()
    };

    private static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(
        string fileName,
        string[] arguments,
        int timeoutSeconds)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        foreach (var arg in arguments)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        try
        {
            process.Start();
            
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            
            var outputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(cts.Token);
            
            await process.WaitForExitAsync(cts.Token);
            
            return (process.ExitCode, await outputTask, await errorTask);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (-1, "", $"Timeout after {timeoutSeconds}s");
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "sdk-chat.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        // Fallback: walk up from test project
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }
}
