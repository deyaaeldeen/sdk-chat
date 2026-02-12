// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using ApiExtractor.Contracts;
using ApiExtractor.DotNet;

var options = CliOptions.Parse(args);

if (options.ShowHelp || options.Path is null)
{
    Console.WriteLine(CliOptions.GetHelpText("C#", "ApiExtractor.DotNet"));
    return options.ShowHelp ? 0 : 1;
}

if (!Directory.Exists(options.Path))
{
    Console.Error.WriteLine($"Directory not found: {options.Path}");
    return 1;
}

var extractor = new CSharpApiExtractor();
var result = await ((IApiExtractor<ApiIndex>)extractor).ExtractAsync(options.Path, CancellationToken.None);

if (result is ExtractorResult<ApiIndex>.Failure failure)
{
    Console.Error.WriteLine($"Error: {failure.Error}");
    return 1;
}

var index = ((ExtractorResult<ApiIndex>.Success)result).Value;
string output;

if (options.OutputJson)
{
    output = extractor.ToJson(index, options.Pretty);
}
else
{
    output = extractor.ToStubs(index);
}

if (options.OutputFile is not null)
{
    await File.WriteAllTextAsync(options.OutputFile, output);
    Console.Error.WriteLine($"Wrote {output.Length:N0} chars to {options.OutputFile}");
}
else
{
    Console.WriteLine(output);
}

return 0;
