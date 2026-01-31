// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SdkChat.Acp;
using Microsoft.SdkChat.Mcp;
using Microsoft.SdkChat.Helpers;
using Microsoft.SdkChat.Models;
using Microsoft.SdkChat.Services;
using Microsoft.SdkChat.Telemetry;
using Microsoft.SdkChat.Tools;
using Microsoft.SdkChat.Tools.Package.Samples;

namespace Microsoft.SdkChat;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Initialize OpenTelemetry tracing (controlled via OTEL_* environment variables)
        TelemetryConfiguration.Initialize();
        
        try
        {
            var rootCommand = new RootCommand("SDK Chat - Sample generation and SDK utilities")
            {
                BuildMcpCommand(),
                BuildAcpCommand(),
                BuildPackageCommand(),
                BuildDoctorCommand()
            };
            
            return await rootCommand.Parse(args).InvokeAsync();
        }
        finally
        {
            // Ensure all traces are flushed before exit
            TelemetryConfiguration.Shutdown();
        }
    }
    
    private static Command BuildPackageCommand()
    {
        var sampleCommand = new Command("sample", "Sample-related commands")
        {
            BuildSampleGenerateCommand()
        };
        
        return new Command("package", "Package-related commands")
        {
            sampleCommand
        };
    }
    
    private static Command BuildMcpCommand()
    {
        var transportOption = new Option<string>("--transport") { Description = "Transport type (stdio, sse)", DefaultValueFactory = _ => "stdio" };
        var portOption = new Option<int>("--port") { Description = "Port for SSE transport", DefaultValueFactory = _ => 8080 };
        var logLevelOption = new Option<string>("--log-level") { Description = "Log level", DefaultValueFactory = _ => "info" };
        var useOpenAiOption = new Option<bool>("--use-openai")
        {
            Description = "Use OpenAI-compatible API instead of GitHub Copilot. " +
                          "Requires OPENAI_API_KEY environment variable."
        };
        var loadDotEnvOption = new Option<bool>("--load-dotenv")
        {
            Description = "Load environment variables from .env in the current directory."
        };

        var command = new Command("mcp", "Start MCP server for AI agent integration (VS Code, Claude Desktop)")
        {
            transportOption,
            portOption,
            logLevelOption,
            useOpenAiOption,
            loadDotEnvOption
        };
        
        command.SetAction(async (parseResult, ct) =>
        {
            if (parseResult.GetValue(loadDotEnvOption))
            {
                DotEnv.TryLoadDefault();
            }

            var transport = parseResult.GetValue(transportOption)!;
            var port = parseResult.GetValue(portOption);
            var logLevel = parseResult.GetValue(logLevelOption)!;
            var useOpenAi = parseResult.GetValue(useOpenAiOption);
            await McpServer.RunAsync(transport, port, logLevel, useOpenAi);
        });
        
        return command;
    }
    
    private static Command BuildAcpCommand()
    {
        var logLevelOption = new Option<string>("--log-level") { Description = "Log level", DefaultValueFactory = _ => "info" };
        var useOpenAiOption = new Option<bool>("--use-openai")
        {
            Description = "Use OpenAI-compatible API instead of GitHub Copilot. " +
                          "Requires OPENAI_API_KEY environment variable."
        };
        var loadDotEnvOption = new Option<bool>("--load-dotenv")
        {
            Description = "Load environment variables from .env in the current directory."
        };

        var command = new Command("acp", "Start ACP agent for interactive sample generation")
        {
            logLevelOption,
            useOpenAiOption,
            loadDotEnvOption
        };
        
        command.SetAction(async (parseResult, ct) =>
        {
            if (parseResult.GetValue(loadDotEnvOption))
            {
                DotEnv.TryLoadDefault();
            }

            var logLevel = parseResult.GetValue(logLevelOption)!;
            var useOpenAi = parseResult.GetValue(useOpenAiOption);
            await SampleGeneratorAgentHost.RunAsync(logLevel, useOpenAi);
        });
        
        return command;
    }
    
    private static Command BuildDoctorCommand()
    {
        var verboseOption = new Option<bool>("--verbose", "-v") { Description = "Show detailed path information" };
        
        var command = new Command("doctor", "Validate external dependencies and report status")
        {
            verboseOption
        };
        
        command.SetAction(async (parseResult, ct) =>
        {
            var verbose = parseResult.GetValue(verboseOption);
            var tool = new DoctorTool();
            Environment.ExitCode = await tool.ExecuteAsync(verbose, ct);
        });
        
        return command;
    }
    
    private static IServiceProvider ConfigureServices(bool useOpenAi)
    {
        var services = new ServiceCollection();
        
        services.AddLogging(builder => builder.AddConsole());
        
        // Configure AI provider settings
        var aiSettings = AiProviderSettings.FromEnvironment();
        if (useOpenAi) aiSettings = aiSettings with { UseOpenAi = true };
        services.AddSingleton(aiSettings);
        
        // Register AI debug logger
        services.AddSingleton<AiDebugLogger>();
        
        // Register AI service (supports both Copilot and OpenAI)
        services.AddSingleton<AiService>();
        services.AddSingleton<IAiService>(sp => sp.GetRequiredService<AiService>());
        services.AddSingleton<FileHelper>();
        services.AddSingleton<ConfigurationHelper>();
        services.AddSingleton<SampleGeneratorTool>();
        
        return services.BuildServiceProvider();
    }
    
    private static Command BuildSampleGenerateCommand()
    {
        var pathArg = new Argument<string>("path") { Description = "Path to SDK root directory (the top-level folder containing .csproj, pyproject.toml, pom.xml, go.mod, or package.json)" };
        var outputOption = new Option<string?>("--output") { Description = "Output directory for samples" };
        var languageOption = new Option<string?>("--language") { Description = "SDK language (dotnet, python, java, typescript, go)" };
        var promptOption = new Option<string?>("--prompt") { Description = "Custom generation prompt" };
        var countOption = new Option<int?>("--count") { Description = "Number of samples to generate (default: 5)" };
        var budgetOption = new Option<int?>("--budget") { Description = $"Max context size in chars (default: {SampleConstants.DefaultContextCharacters / 1000}K)" };
        var modelOption = new Option<string?>("--model") 
        { 
            Description = $"AI model (default: {AiProviderSettings.DefaultCopilotModel} for Copilot, {AiProviderSettings.DefaultOpenAiModel} for OpenAI)"
        };
        var dryRunOption = new Option<bool>("--dry-run") { Description = "Preview without writing files" };
        var useOpenAiOption = new Option<bool>("--use-openai")
        {
            Description = "Use OpenAI-compatible API instead of GitHub Copilot. " +
                          "Requires OPENAI_API_KEY environment variable."
        };
        var loadDotEnvOption = new Option<bool>("--load-dotenv")
        {
            Description = "Load environment variables from .env in the current directory."
        };

        var command = new Command("generate", "Generate code samples for SDK package")
        {
            pathArg,
            outputOption,
            languageOption,
            promptOption,
            countOption,
            budgetOption,
            modelOption,
            dryRunOption,
            useOpenAiOption,
            loadDotEnvOption
        };
        
        command.SetAction(async (parseResult, ct) =>
        {
            if (parseResult.GetValue(loadDotEnvOption))
            {
                DotEnv.TryLoadDefault();
            }

            var path = parseResult.GetValue(pathArg)!;
            var output = parseResult.GetValue(outputOption);
            var language = parseResult.GetValue(languageOption);
            var prompt = parseResult.GetValue(promptOption);
            var count = parseResult.GetValue(countOption);
            var budget = parseResult.GetValue(budgetOption);
            var model = parseResult.GetValue(modelOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            var useOpenAi = parseResult.GetValue(useOpenAiOption);
            
            var services = ConfigureServices(useOpenAi);
            var tool = services.GetRequiredService<SampleGeneratorTool>();
            Environment.ExitCode = await tool.ExecuteAsync(path, output, language, prompt, count, budget, model, dryRun, ct);
        });
        
        return command;
    }
}
