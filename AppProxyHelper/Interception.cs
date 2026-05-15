namespace AppProxyHelper;

public interface IInterceptionSession : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken);
    void SetTargetProcess(int processId, string executablePath);
}

public static class TrafficInterceptionFactory
{
    public static IInterceptionSession Create(LoadedConfig loadedConfig, AppLogger logger)
    {
        return loadedConfig.Value.Mode.Equals("Transparent", StringComparison.OrdinalIgnoreCase)
            ? new WinDivertInterceptionSession(loadedConfig, logger)
            : new EnvironmentInterceptionSession(loadedConfig.Value, logger);
    }
}

internal sealed class EnvironmentInterceptionSession : IInterceptionSession
{
    private readonly AppProxyConfig _config;
    private readonly AppLogger _logger;

    public EnvironmentInterceptionSession(AppProxyConfig config, AppLogger logger)
    {
        _config = config;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_config.InjectProxyEnvironment)
        {
            _logger.Info("Environment 模式: 将通过目标进程环境变量注入代理配置。");
        }
        else
        {
            _logger.Warn("Environment 模式启用，但 injectProxyEnvironment=false；目标应用不会被注入代理变量。");
        }

        _logger.Warn("Environment 模式不是透明全流量拦截；不读取代理环境变量的应用不会被强制代理。");
        return Task.CompletedTask;
    }

    public void SetTargetProcess(int processId, string executablePath)
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
