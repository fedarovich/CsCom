namespace CsCom;

internal static class Program
{
    private static int Main(string[] args) => CommandLine.Create().Parse(args).Invoke();
}
