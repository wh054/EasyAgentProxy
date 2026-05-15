using System.Diagnostics;
using System.Text;

namespace AppProxyHelper;

public sealed class ProcessProxyLauncher
{
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
        var targetPath = PathResolver.Resolve(_config.TargetPath, _loadedConfig.BaseDirectory);
        if (string.IsNullOrWhiteSpace(_config.TargetPath) || !File.Exists(targetPath))
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

        if (_config.InjectProxyEnvironment)
        {
            InjectProxyEnvironment(startInfo);
        }

        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        _logger.Info($"启动目标程序: {targetPath}");
        _logger.Info($"工作目录: {startInfo.WorkingDirectory}");
        _logger.Debug($"参数数量: {startInfo.ArgumentList.Count}");

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

    private void InjectProxyEnvironment(ProcessStartInfo startInfo)
    {
        var proxy = ProxyEndpoint.Parse(_config.ProxyUri);
        var proxyValue = proxy.ToUriString();
        var noProxy = string.Join(",", _config.NoProxy.Where(static item => !string.IsNullOrWhiteSpace(item)));

        SetEnvironment(startInfo, "HTTP_PROXY", proxyValue);
        SetEnvironment(startInfo, "HTTPS_PROXY", proxyValue);
        SetEnvironment(startInfo, "ALL_PROXY", proxyValue);
        SetEnvironment(startInfo, "http_proxy", proxyValue);
        SetEnvironment(startInfo, "https_proxy", proxyValue);
        SetEnvironment(startInfo, "all_proxy", proxyValue);

        if (!string.IsNullOrWhiteSpace(noProxy))
        {
            SetEnvironment(startInfo, "NO_PROXY", noProxy);
            SetEnvironment(startInfo, "no_proxy", noProxy);
        }

        _logger.Info($"已注入代理环境变量: {UriFormatter.Sanitize(proxyValue)}");
        if (!string.IsNullOrWhiteSpace(noProxy))
        {
            _logger.Info($"已注入 NO_PROXY: {noProxy}");
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

                _logger.Info($"target/{name}: {line}");
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
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
