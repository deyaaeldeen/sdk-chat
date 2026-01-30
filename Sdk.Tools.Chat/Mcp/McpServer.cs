using ModelContextProtocol.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sdk.Tools.Chat.Helpers;
using Sdk.Tools.Chat.Models;
using Sdk.Tools.Chat.Services;
using Sdk.Tools.Chat.Services.Languages;

namespace Sdk.Tools.Chat.Mcp;

/// <summary>
/// MCP server implementation for AI agent integration.
/// Exposes sdk-chat tools to VS Code Copilot, Claude Desktop, etc.
/// </summary>
public static class McpServer
{
    public static async Task RunAsync(string transport, int port, string logLevel)
    {
        var builder = Host.CreateApplicationBuilder();
        
        // Configure logging
        builder.Logging.AddConsole().SetMinimumLevel(ParseLogLevel(logLevel));
        
        // Configure services
        builder.Services.AddSingleton(AiProviderSettings.FromEnvironment());
        builder.Services.AddSingleton<AiDebugLogger>();
        builder.Services.AddSingleton<AiService>();
        builder.Services.AddSingleton<IAiService>(sp => sp.GetRequiredService<AiService>());
        builder.Services.AddSingleton<FileHelper>();
        builder.Services.AddSingleton<ConfigurationHelper>();
        
        // Configure MCP server
        builder.Services.AddMcpServer(options =>
        {
            options.ServerInfo = new() { Name = "sdk-chat", Version = "1.0.0" };
        })
        .WithStdioServerTransport()
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
