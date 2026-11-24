using System.Text;

namespace CsCom;

internal static class Program
{
    private static int Main(string[] args)
    {
        // The MSBuild tasks of CsCom.Build read the output of their commands as UTF-8.
        if (args is [CommandLine.BuildGenerateCommandName or CommandLine.BuildExportCommandName, ..])
        {
            Console.OutputEncoding = Encoding.UTF8;
        }

        return CommandLine.Create().Parse(args).Invoke();
    }
}
