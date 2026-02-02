using System.CommandLine;

namespace Microsoft.SdkChat.Commands;

public class PackageCommand : Command
{
    public PackageCommand() : base("package", "Package-related commands")
    {
        var sampleCommand = new Command("sample", "Sample-related commands");
        sampleCommand.Add(new GenerateCommand());
        Add(sampleCommand);
    }
}
