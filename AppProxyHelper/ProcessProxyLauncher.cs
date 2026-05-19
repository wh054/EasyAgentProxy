using System.Diagnostics;
using System.Text;

namespace AppProxyHelper;

public sealed class ProcessProxyLauncher
{
    private const int MaxCapturedOutputLineChars = 4096;
    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OutputStopTimeout = TimeSpan.FromSeconds(1);

    private readonly LoadedConfig _loadedConfig;
    private readonly AppProxyConfig _config;
    private readonly AppLogger _logger;
    private readonly IInterceptionSession? _interception;

    public ProcessProxyLauncher(LoadedConfig loadedConfig, AppLogger logger, IInterceptionSession? interception = null)
    {
        _loadedConfig = loadedConfig;
        _config = loadedConfig.Value;
        _logger = logger;
        _interception = interception;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_config.TargetPath))
        {
            _logger.Error("目标程序不存在: targetPath 为空。Environment 模式必须由 EasyProxy 启动目标程序，才能注入代理配置。");
            return 2;
        }

        var targetPath = PathResolver.Resolve(_config.TargetPath, _loadedConfig.BaseDirectory);
        if (!File.Exists(targetPath))
        {
            _logger.Error($"目标程序不存在: {targetPath}");
            return 2;
        }

        var startInfo = new ProcessStartInfo(targetPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = _config.CaptureChildOutput,
            RedirectStandardError = _config.CaptureChildOutput,
            StandardOutputEncoding = _config.CaptureChildOutput ? Encoding.UTF8 : null,
            StandardErrorEncoding = _config.CaptureChildOutput ? Encoding.UTF8 : null
        };

        foreach (var argument in _config.TargetArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrWhiteSpace(_config.WorkingDirectory))
        {
            startInfo.WorkingDirectory = PathResolver.Resolve(_config.WorkingDirectory, _loadedConfig.BaseDirectory);
        }
        else
        {
            startInfo.WorkingDirectory = Path.GetDirectoryName(targetPath) ?? _loadedConfig.BaseDirectory;
        }

        ProxyEnvironmentProfile? profile = null;
        if (_config.InjectProxyEnvironment)
        {
            profile = InjectProxyEnvironment(startInfo);
        }

        if (profile is not null && AntigravityCloudCodeRelay.IsAntigravityExecutable(targetPath))
        {
            await AntigravityCloudCodeRelay.EnsureStartedAsync(
                profile.HttpProxyUri,
                _logger,
                cancellationToken);
        }

        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        _logger.Info($"启动目标程序: {targetPath}");
        _logger.Info($"工作目录: {startInfo.WorkingDirectory}");
        _logger.Debug($"参数数量: {startInfo.ArgumentList.Count}");
        WarnIfLaunchingPackagedAppDirectly(targetPath);

        if (!process.Start())
        {
            _logger.Error("目标程序启动失败，Process.Start 返回 false。");
            return 1;
        }

        _logger.Info($"目标程序已启动: pid={process.Id}");
        _interception?.SetTargetProcess(process.Id, targetPath);

        var outputTask = _config.CaptureChildOutput
            ? CaptureOutputAsync(process, cancellationToken)
            : Task.CompletedTask;

        if (!_config.WaitForExit)
        {
            _logger.Info("waitForExit=false，工具不会等待目标程序退出。");
            return 0;
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            if (_config.CaptureChildOutput)
            {
                await DrainCapturedOutputAsync(process, outputTask, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Warn("等待目标进程退出时被取消。");
            return 130;
        }

        _logger.Info($"目标程序已退出: pid={process.Id}, exitCode={process.ExitCode}");
        return process.ExitCode;
    }

    private ProxyEnvironmentProfile InjectProxyEnvironment(ProcessStartInfo startInfo)
    {
        var profile = ProxyEnvironmentProfile.Create(_config.ProxyUri, _config.NoProxy);
        var noProxy = profile.NoProxyValue;

        foreach (var variable in profile.GetEnvironmentVariables())
        {
            SetEnvironment(startInfo, variable.Key, variable.Value);
        }

        var antigravityRelayUrl = AntigravityCloudCodeRelay.IsAntigravityExecutable(startInfo.FileName)
            ? AntigravityCloudCodeRelay.RelayUrl
            : null;
        foreach (var settingsPath in KnownEditorProxySettings.WriteForExecutable(
            startInfo.FileName,
            profile.HttpProxyUri,
            antigravityRelayUrl))
        {
            _logger.Info($"已同步应用代理设置: {settingsPath}");
        }

        _logger.Info(
            "已注入代理环境变量: " +
            $"HTTP(S)_PROXY={UriFormatter.Sanitize(profile.HttpProxyUri)}, " +
            $"ALL_PROXY={UriFormatter.Sanitize(profile.AllProxyUri)}");
        if (!string.IsNullOrWhiteSpace(profile.NoProxyValue))
        {
            _logger.Info($"已注入 NO_PROXY: {noProxy}");
        }

        return profile;
    }

    private void WarnIfLaunchingPackagedAppDirectly(string targetPath)
    {
        if (targetPath.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Warn(
                "当前 targetPath 位于 WindowsApps，EasyProxy 会直接以 exe 方式启动目标程序。" +
                "部分 MSIX/Store 应用可能缺少正常应用激活上下文；如果目标日志出现 helper paths unavailable，建议优先使用 EasyProxy 识别到的 Codex/Cursor/Antigravity 安装路径，或改用非 Store 版本。");
        }
    }

    private static void SetEnvironment(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.Environment[name] = value;
    }

    private async Task CaptureOutputAsync(Process process, CancellationToken cancellationToken)
    {
        var stdout = PumpOutputAsync("stdout", process.StandardOutput, cancellationToken);
        var stderr = PumpOutputAsync("stderr", process.StandardError, cancellationToken);
        await Task.WhenAll(stdout, stderr);
    }

    private async Task DrainCapturedOutputAsync(Process process, Task outputTask, CancellationToken cancellationToken)
    {
        try
        {
            await outputTask.WaitAsync(OutputDrainTimeout, cancellationToken);
            return;
        }
        catch (TimeoutException)
        {
            _logger.Warn("目标进程已退出，但输出管道未关闭；将停止等待输出，避免界面卡住。");
        }

        CloseCapturedOutput(process);

        try
        {
            await outputTask.WaitAsync(OutputStopTimeout);
        }
        catch (TimeoutException)
        {
            _logger.Warn("输出捕获任务停止超时，已后台忽略。");
            ObserveFault(outputTask);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }

    private async Task PumpOutputAsync(string name, TextReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync();
                if (line is null)
                {
                    return;
                }

                _logger.Info($"target/{name}: {FormatCapturedOutputLine(line)}");
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static string FormatCapturedOutputLine(string line)
    {
        if (line.Length <= MaxCapturedOutputLineChars)
        {
            return line;
        }

        return line[..MaxCapturedOutputLineChars] +
            $"... [truncated {line.Length - MaxCapturedOutputLineChars} chars]";
    }

    private void CloseCapturedOutput(Process process)
    {
        try
        {
            process.StandardOutput.Close();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            process.StandardError.Close();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void ObserveFault(Task task)
    {
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

}
