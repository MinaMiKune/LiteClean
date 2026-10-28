namespace LiteClean.Tests;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Any(static arg => string.Equals(arg, "--benchmark", StringComparison.OrdinalIgnoreCase)))
        {
            return Benchmark.Run(args);
        }

        var filter = ReadOption(args, "--filter");
        var runner = new TestRunner(filter);
        return runner.RunAll();
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }
}
