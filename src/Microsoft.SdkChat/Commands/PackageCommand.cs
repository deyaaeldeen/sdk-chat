using System.CommandLine;

namespace Microsoft.SdkChat.Commands;

public class PackageCommand : Command
{
    public PackageCommand() : base("package", "SDK package operations")
    {
        // Entity-based command structure:
        // package source detect <path>
        // package samples detect <path>
        // package samples generate <path>
        // package api extract <path>
        // package api coverage <path>
        Add(new SourceEntityCommand());
        Add(new SamplesEntityCommand());
        Add(new ApiEntityCommand());
    }
}
