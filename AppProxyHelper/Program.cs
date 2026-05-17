using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows.Forms;
using AppProxyHelper;

internal static class Program
{
    private const int ErrorCancelled = 1223;
    private const string ElevatedStartupOption = "--elevated-startup";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0].Equals("ui", StringComparison.OrdinalIgnoreCase))
        {
            if (TryRelaunchGuiAsAdministrator(args))
            {
                return 0;
            }

            AppProxyGui.Run(
                GetGuiConfigPath(args),
                HasOption(args, "--autorun"),
                HasOption(args, "--repair-env"),
                HasOption(args, ElevatedStartupOption));
            return 0;
        }

        return Cli.RunAsync(args).GetAwaiter().GetResult();
    }

    private static bool TryRelaunchGuiAsAdministrator(string[] args)
    {
        if (!OperatingSystem.IsWindows() || IsAdministrator())
        {
            return false;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            executablePath = Application.ExecutablePath;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Environment.CurrentDirectory
            };

            if (args.Length == 0)
            {
                startInfo.ArgumentList.Add("ui");
            }
            else
            {
                foreach (var arg in args)
                {
                    if (!arg.Equals(ElevatedStartupOption, StringComparison.OrdinalIgnoreCase))
                    {
                        startInfo.ArgumentList.Add(arg);
                    }
                }
            }

            startInfo.ArgumentList.Add(ElevatedStartupOption);

            if (Process.Start(startInfo) is null)
            {
                MessageBox.Show(
                    "无法启动管理员权限实例。",
                    "请求管理员权限失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }

            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "请求管理员权限失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return true;
        }
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
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
