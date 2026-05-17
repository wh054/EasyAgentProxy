using AppProxyHelper;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0].Equals("ui", StringComparison.OrdinalIgnoreCase))
        {
            AppProxyGui.Run(
                GetGuiConfigPath(args),
                HasOption(args, "--autorun"),
                HasOption(args, "--repair-env"));
            return 0;
        }

        return Cli.RunAsync(args).GetAwaiter().GetResult();
    }

    private static string? GetGuiConfigPath(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--config", StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static bool HasOption(string[] args, string optionName)
    {
        return args.Any(arg => arg.Equals(optionName, StringComparison.OrdinalIgnoreCase));
    }
}
