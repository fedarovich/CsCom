using System.Text;

namespace CsCom;

internal static class Program
{
    private static int Main(string[] args)
    {
        // The MSBuild task of CsCom.Build reads the output of its command as UTF-8.
        if (args is [CommandLine.BuildGenerateCommandName, ..])
        {
            Console.OutputEncoding = Encoding.UTF8;
        }

        return CommandLine.Create().Parse(args).Invoke();
    }
}
