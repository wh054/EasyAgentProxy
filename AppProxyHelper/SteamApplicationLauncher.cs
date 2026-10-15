using System.Diagnostics;

namespace AppProxyHelper;

internal static class SteamApplicationLauncher
{
    internal static bool IsSteamExecutable(string path) =>
        Path.GetFileName(path).Equals("steam.exe", StringComparison.OrdinalIgnoreCase);

    internal static ProcessStartInfo CreateStartInfo(string executablePath, IEnumerable<string>? arguments = null, string? workingDirectory = null)
    {
        var path = Path.GetFullPath(executablePath);
        var info = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Path.GetDirectoryName(path)! : workingDirectory
        };
        // ConfigLoader adds these for Electron apps. Steam uses the hosts reverse proxy.
        foreach (var argument in arguments ?? [])
        {
            if (argument.Equals("--disable-quic", StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith("--proxy-server=", StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith("--proxy-bypass-list=", StringComparison.OrdinalIgnoreCase)) continue;
            info.ArgumentList.Add(argument);
        }
        return info;
    }

    internal static async Task<int> LaunchAsync(string executablePath, string? upstreamProxyUri,
        IEnumerable<string>? arguments = null, string? workingDirectory = null,
        Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(executablePath);
        if (!IsSteamExecutable(path) || !File.Exists(path))
            throw new FileNotFoundException("未找到 Steam.exe，请检查 Steam 安装路径。", path);
        if (upstreamProxyUri is not null) SteamProxyCli.ValidateProxyUri(upstreamProxyUri);
        var info = CreateStartInfo(path, arguments, workingDirectory);
        var controller = new SteamProxyProcessController();
        log?.Invoke("正在准备临时 Steam 代理会话；首次运行需安装当前用户 CA，写入 Hosts 需要提权。");
        var previous = await Task.Run(controller.GetStatus, cancellationToken);
        var session = await controller.EnsureSessionStartedAsync(path, upstreamProxyUri, cancellationToken);
        var createdSession = previous.ProcessId != session.ProcessId;
        Process? process = null;
        var launched = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            process = Process.Start(info) ?? throw new InvalidOperationException("Steam 启动失败。");
            launched = true;
            log?.Invoke($"Steam 已启动: pid={process.Id}。Steam 完全退出后将自动恢复 Hosts、清理临时配置；CA 保留供下次复用。");
            // Steam.exe may hand off to an existing client or replace itself while updating.
            // Watching just this Process would undo the proxy while Steam is still running.
            await SteamProcessLifetime.WaitForExitAsync(path, process, cancellationToken);
            return process.HasExited ? process.ExitCode : 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            log?.Invoke("已取消 Steam 代理会话，正在恢复原网络状态；Steam 进程不会被结束。");
            return 130;
        }
        finally
        {
            process?.Dispose();
            if (launched || createdSession)
            {
                var current = await Task.Run(controller.GetStatus);
                if (!current.IsRunning || current.IsSession && current.ProcessId == session.ProcessId)
                    await controller.StopAsync(CancellationToken.None);
            }
            log?.Invoke("Steam 代理会话已结束，已撤销本次网络接管。");
        }
    }
}
