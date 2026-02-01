using ModelContextProtocol.Server;
using ModelContextProtocol.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.SdkChat.Configuration;
using Microsoft.SdkChat.Helpers;
using Microsoft.SdkChat.Models;
using Microsoft.SdkChat.Services;
using Microsoft.SdkChat.Services.Languages;

namespace Microsoft.SdkChat.Mcp;

/// <summary>
/// MCP server implementation for AI agent integration.
/// Exposes sdk-chat tools to VS Code Copilot, Claude Desktop, etc.
/// </summary>
public static class McpServer
{
    /// <summary>
    /// Supported transport types for the MCP server.
    /// </summary>
    private static readonly HashSet<string> SupportedTransports = new(StringComparer.OrdinalIgnoreCase)
    {
        "stdio",
        "sse"
    };
    
    public static async Task RunAsync(string transport, int port, string logLevel, bool useOpenAi = false)
    {
        // Validate transport type - fail fast if unsupported
        if (!SupportedTransports.Contains(transport))
        {
            throw new NotSupportedException(
                $"Transport '{transport}' is not supported. Supported transports: {string.Join(", ", SupportedTransports)}.");
        }
        
        // For SSE transport, use WebApplication for HTTP/SSE support
        if (transport.Equals("sse", StringComparison.OrdinalIgnoreCase))
        {
            var builder = WebApplication.CreateBuilder();
            
            // Configure logging
            builder.Logging.AddConsole().SetMinimumLevel(ParseLogLevel(logLevel));
            
            // Configure centralized options - fail fast on validation errors
            var options = SdkChatOptions.FromEnvironment();
            if (useOpenAi) options.UseOpenAi = true;
            
            // Validate configuration at startup
            var validationResults = options.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(options));
            var errors = validationResults.ToList();
            if (errors.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Configuration validation failed: {string.Join("; ", errors.Select(e => e.ErrorMessage))}");
            }
            
            builder.Services.AddSingleton(options);
            
            // Legacy compatibility - create AiProviderSettings from SdkChatOptions
            var aiSettings = new AiProviderSettings
            {
                UseOpenAi = options.UseOpenAi,
                Endpoint = options.Endpoint,
                ApiKey = options.ApiKey,
                Model = options.Model,
                DebugEnabled = options.DebugEnabled,
                DebugDirectory = options.DebugDirectory
            };
            builder.Services.AddSingleton(aiSettings);
            
            // Register services
            builder.Services.AddSingleton<ProcessSandboxService>();
            builder.Services.AddSingleton<AiDebugLogger>();
            builder.Services.AddSingleton<AiService>();
            builder.Services.AddSingleton<IAiService>(sp => sp.GetRequiredService<AiService>());
            builder.Services.AddSingleton<FileHelper>();
            builder.Services.AddSingleton<ConfigurationHelper>();
            
            // Configure MCP server with SSE transport
            builder.Services.AddMcpServer(options =>
            {
                options.ServerInfo = new() { Name = "sdk-chat", Version = "1.0.0" };
            })
            .WithHttpTransport()
            .WithToolsFromAssembly();
            
            var app = builder.Build();
            
            // Configure endpoint
            app.Urls.Add($"http://localhost:{port}");
            
            app.MapMcp();
            await app.RunAsync();
        }
        else // stdio transport
        {
            var builder = Host.CreateApplicationBuilder();
            
            // Configure logging
            builder.Logging.AddConsole().SetMinimumLevel(ParseLogLevel(logLevel));
            
            // Configure centralized options - fail fast on validation errors
            var options = SdkChatOptions.FromEnvironment();
            if (useOpenAi) options.UseOpenAi = true;
            
            // Validate configuration at startup
            var validationResults = options.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(options));
            var errors = validationResults.ToList();
            if (errors.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Configuration validation failed: {string.Join("; ", errors.Select(e => e.ErrorMessage))}");
            }
            
            builder.Services.AddSingleton(options);
            
            // Legacy compatibility - create AiProviderSettings from SdkChatOptions
            var aiSettings = new AiProviderSettings
            {
                UseOpenAi = options.UseOpenAi,
                Endpoint = options.Endpoint,
                ApiKey = options.ApiKey,
                Model = options.Model,
                DebugEnabled = options.DebugEnabled,
                DebugDirectory = options.DebugDirectory
            };
            builder.Services.AddSingleton(aiSettings);
            
            // Register services
            builder.Services.AddSingleton<ProcessSandboxService>();
            builder.Services.AddSingleton<AiDebugLogger>();
            builder.Services.AddSingleton<AiService>();
            builder.Services.AddSingleton<IAiService>(sp => sp.GetRequiredService<AiService>());
            builder.Services.AddSingleton<FileHelper>();
            builder.Services.AddSingleton<ConfigurationHelper>();
            
            // Configure MCP server with stdio transport
            builder.Services.AddMcpServer(options =>
            {
                options.ServerInfo = new() { Name = "sdk-chat", Version = "1.0.0" };
            })
            .WithStdioServerTransport()
            .WithToolsFromAssembly();
            
            var host = builder.Build();
            await host.RunAsync();
        }
    }
    
    private static LogLevel ParseLogLevel(string level) => level.ToLowerInvariant() switch
    {
        "debug" => LogLevel.Debug,
        "warning" => LogLevel.Warning,
        "error" => LogLevel.Error,
        _ => LogLevel.Information
    };
}
