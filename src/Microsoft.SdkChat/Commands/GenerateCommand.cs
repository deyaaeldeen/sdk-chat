using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SdkChat.Configuration;
using Microsoft.SdkChat.Helpers;
using Microsoft.SdkChat.Tools.Package.Samples;

namespace Microsoft.SdkChat.Commands;

public class GenerateCommand : Command
{
    public GenerateCommand() : base("generate", "Generate code samples for SDK package")
    {
        var pathArg = new Argument<string>("path") { Description = "Path to SDK root directory" };
        var output = new Option<string?>("--output") { Description = "Output directory for samples" };
        var language = new Option<string?>("--language") { Description = "SDK language (dotnet, python, java, typescript, go)" };
        var prompt = new Option<string?>("--prompt") { Description = "Custom generation prompt" };
        var count = new Option<int?>("--count") { Description = "Number of samples to generate (default: 5)" };
        var budget = new Option<int?>("--budget") { Description = "Max context size in chars (default: 128K)" };
        var model = new Option<string?>("--model") { Description = "AI model" };
        var dryRun = new Option<bool>("--dry-run") { Description = "Preview without writing files" };
        var useOpenAi = new Option<bool>("--use-openai") { Description = "Use OpenAI-compatible API" };
        var loadDotEnv = new Option<bool>("--load-dotenv") { Description = "Load environment variables from .env" };

        Add(pathArg);
        Add(output);
        Add(language);
        Add(prompt);
        Add(count);
        Add(budget);
        Add(model);
        Add(dryRun);
        Add(useOpenAi);
        Add(loadDotEnv);

        this.SetAction(async (ctx, ct) =>
        {
            if (ctx.GetValue(loadDotEnv)) DotEnv.TryLoadDefault();

            var services = ServiceBuilder.Build(ctx.GetValue(useOpenAi));
            var tool = services.GetRequiredService<SampleGeneratorTool>();

            Environment.ExitCode = await tool.ExecuteAsync(
                ctx.GetValue(pathArg)!,
                ctx.GetValue(output),
                ctx.GetValue(language),
                ctx.GetValue(prompt),
                ctx.GetValue(count),
                ctx.GetValue(budget),
                ctx.GetValue(model),
                ctx.GetValue(dryRun),
                ct
            );
        });
    }
}
