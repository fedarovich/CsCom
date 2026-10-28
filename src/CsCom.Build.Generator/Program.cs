using System.Text;
using CsCom;
using CsCom.LibrarySets;
using Microsoft.Extensions.Logging;

// Generates the code of the CsComReference items of a project for the MSBuild task of CsCom.Build:
// CsCom.Build.Generator <request.json> <hash>. The messages are written to the standard output in the canonical format of
// MSBuild, so that the task reports the warnings and errors.
Console.OutputEncoding = Encoding.UTF8;
if (args.Length != 2)
{
    Console.WriteLine(MSBuildOutput.FormatError(new CsComException("Usage: CsCom.Build.Generator <request.json> <hash>")));
    return 1;
}

using var loggerFactory = LoggerFactory.Create(builder => builder
    .SetMinimumLevel(LogLevel.Information)
    .AddProvider(new MSBuildOutput.LoggerProvider(Console.Out, LogLevel.Information)));
try
{
    var request = BuildRequest.Load(args[0]);
    new LibrarySetBuild(new InteropPipeline(loggerFactory), loggerFactory).Run(request, args[1]);
    return 0;
}
catch (Exception e)
{
    Console.WriteLine(MSBuildOutput.FormatError(e));
    return 1;
}
