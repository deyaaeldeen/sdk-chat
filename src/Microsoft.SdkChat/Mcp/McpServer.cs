using ModelContextProtocol.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
        "stdio"
    };
    
    public static async Task RunAsync(string transport, int port, string logLevel, bool useOpenAi = false)
    {
        // Validate transport type - fail fast if unsupported
        if (!SupportedTransports.Contains(transport))
        {
            throw new NotSupportedException(
                $"Transport '{transport}' is not supported. Supported transports: {string.Join(", ", SupportedTransports)}. " +
                $"SSE transport is planned for a future release.");
        }
        
        var builder = Host.CreateApplicationBuilder();
        
        // Configure logging
        builder.Logging.AddConsole().SetMinimumLevel(ParseLogLevel(logLevel));
        
        // Configure services
        var aiSettings = AiProviderSettings.FromEnvironment();
        if (useOpenAi) aiSettings = aiSettings with { UseOpenAi = true };
        builder.Services.AddSingleton(aiSettings);
        builder.Services.AddSingleton<AiDebugLogger>();
        builder.Services.AddSingleton<AiService>();
        builder.Services.AddSingleton<IAiService>(sp => sp.GetRequiredService<AiService>());
        builder.Services.AddSingleton<FileHelper>();
        builder.Services.AddSingleton<ConfigurationHelper>();
        
        // Configure MCP server with validated transport
        builder.Services.AddMcpServer(options =>
        {
            options.ServerInfo = new() { Name = "sdk-chat", Version = "1.0.0" };
        })
        .WithStdioServerTransport()  // Currently only stdio is supported
        .WithToolsFromAssembly();
        
        var host = builder.Build();
        await host.RunAsync();
    }
    
    private static LogLevel ParseLogLevel(string level) => level.ToLowerInvariant() switch
    {
        "debug" => LogLevel.Debug,
        "warning" => LogLevel.Warning,
        "error" => LogLevel.Error,
        _ => LogLevel.Information
    };
}
