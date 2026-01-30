using System.Text.Json;
using Sdk.Tools.Chat.Models;

namespace Sdk.Tools.Chat.Helpers;

public class ConfigurationHelper
{
    private const string ConfigFileName = "sdk-chat-config.json";
    
    public async Task<SdkChatConfig?> TryLoadConfigAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        var configPath = Path.Combine(packagePath, ConfigFileName);
        
        if (!File.Exists(configPath))
            return null;
        
        var json = await File.ReadAllTextAsync(configPath, cancellationToken);
        return JsonSerializer.Deserialize<SdkChatConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }

    public async Task<SdkChatConfig> LoadConfigAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        return await TryLoadConfigAsync(packagePath, cancellationToken) ?? new SdkChatConfig();
    }
}
