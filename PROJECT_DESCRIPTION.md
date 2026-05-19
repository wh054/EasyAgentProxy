# EasyProxy 项目说明

EasyProxy 当前定位为 Electron/Chromium 应用代理启动器，优先服务 Codex、Cursor、Antigravity 这类桌面 AI 应用。

## 产品方向

过去的 Transparent 方案依赖 WinDivert 做系统级透明拦截。实际测试中，Codex 在关闭全局 TUN 后仍会出现首包慢、重连、QUIC/UDP 绕行和 WinDivert 驱动兼容性问题。对当前目标来说，Transparent 没有比显式 Chromium 代理参数带来更稳定的收益。

现在产品入口收敛为：

1. 由 EasyProxy 启动目标 exe。
2. 固定使用 `Environment` 方案。
3. 自动生成并维护 Chromium 代理参数。
4. 注入常见代理环境变量。
5. 启动前做本地代理和 TLS 诊断。
6. 可生成 Codex、Cursor、Antigravity 的独立代理启动脚本，日常使用不需要先打开 EasyProxy。

## 自动代理参数

保存或运行配置时，EasyProxy 会把以下参数整理到 `targetArguments` 前面，并保留额外启动参数：

- `--disable-quic`
- `--proxy-server=<proxyUri>`
- `--proxy-bypass-list=localhost;127.0.0.1;::1;<local>`

这能避免 Electron/Chromium 优先尝试 QUIC/UDP 或绕过环境变量导致首包慢、重连的问题。

## 保留的核心配置

- `targetPath`
- `targetArguments`
- `workingDirectory`
- `proxyUri`
- `logDirectory`
- `minimumLogLevel`
- `captureChildOutput`
- `waitForExit`
- `runDiagnosticsBeforeLaunch`
- `abortLaunchWhenDiagnosticsFail`
- `noProxy`
- `diagnostics`

`targetProcessNames`、运行中进程选择、Transparent/UDP/WinDivert 配置不再作为产品入口存在。

## 主要文件

- `AppProxyHelper/AppProxyConfig.cs`：配置模型。
- `AppProxyHelper/ConfigLoader.cs`：配置加载、保存、验证和 Chromium 参数整理。
- `AppProxyHelper/ProcessProxyLauncher.cs`：目标进程启动和环境变量注入。
- `AppProxyHelper/ProxyEnvironmentProfile.cs`：统一生成 Chromium 参数、HTTP(S)/gRPC 代理环境变量和 no-proxy 变量。
- `AppProxyHelper/KnownEditorProxySettings.cs`：同步 Cursor、Antigravity 的 VS Code 风格用户代理设置。
- `AppProxyHelper/ProxyLauncherScriptGenerator.cs`：代理启动脚本生成。
- `AppProxyHelper/TargetApplicationCatalog.cs`：Codex、Cursor、Antigravity 自动发现。
- `AppProxyHelper/AppProxyGui.cs`：WinForms GUI。
- `AppProxyHelper/Cli.cs`：命令行入口。
- `app-proxy.example.json`：Electron-first 示例配置。

Transparent 相关实现代码暂时留在仓库中作为历史实现，但不再由配置、GUI、CLI 或打包流程进入。
