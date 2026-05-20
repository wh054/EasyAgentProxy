# EasyProxy 项目说明

EasyProxy 当前定位为 Electron/Chromium 桌面 AI 应用的代理启动器，优先服务 Codex、Cursor、Antigravity 和 Antigravity IDE。

## 产品方向

早期 Transparent 方案依赖 WinDivert 做系统级透明拦截。实际验证中，Codex 和 Antigravity 在关闭全局 TUN 后会遇到首包慢、重连、QUIC/UDP 绕行、WinDivert 驱动兼容等问题。对当前目标应用来说，Transparent 没有比显式 Chromium 代理参数和环境变量更稳定。

现在产品入口收敛为：

1. 固定使用 `Environment` 方案。
2. 由 EasyProxy 或生成的代理脚本启动目标应用。
3. 注入 Chromium 代理参数和代理环境变量。
4. 为已知编辑器同步 VS Code 风格用户设置。
5. 生成独立 `*-Proxy` 快捷方式，不覆盖原应用图标。
6. 脚本放进应用目录下的 `EasyProxy` 子目录，不堆在桌面。

## 应用处理策略

### Codex

Codex 使用 Chromium 参数和环境变量。由于 Microsoft Store/WindowsApps 目录通常不可写，代理脚本放到：

```text
%LOCALAPPDATA%\Packages\OpenAI.Codex_2p2nqsd0c76g0\LocalCache\EasyProxy
```

生成入口为 `Codex-Proxy.lnk`。

### Cursor

Cursor 使用 Chromium 参数、环境变量和用户 settings 注入。脚本放到：

```text
%LOCALAPPDATA%\Programs\cursor\EasyProxy
```

生成入口为 `Cursor-Proxy.lnk`。

### Antigravity

新版 Antigravity 除了 Chromium 参数和环境变量，还需要 CloudCode relay 与 language server shim。

链路：

```text
Antigravity-Proxy.lnk
  -> AntigravityProxy.vbs
  -> AntigravityProxy.cmd
  -> Node CloudCode relay on 127.0.0.1:18990
  -> Antigravity.exe
  -> EasyProxy language_server.exe shim
  -> language_server.easyproxy-original.exe
```

shim 会把 CloudCode endpoint 改到本机 relay，relay 再通过用户代理访问 Google CloudCode/Google APIs。
relay 会保留脱敏日志 `Antigravity CloudCode Relay.log`，用于排查请求是否到达上游、耗时和 HTTP 状态。启动阶段的 `v1internal:onboardUser` 偶尔会因为上游响应超过 Antigravity 的短超时而失败，relay 会对该接口做一次备用 upstream 重试。

应用更新可能覆盖 shim。恢复方式是重新运行 EasyProxy 的脚本生成功能。

### Antigravity IDE

Antigravity IDE 是旧版/IDE 版，安装目录和运行结构更接近 VS Code。它的 language server 位于：

```text
resources\app\extensions\antigravity\bin\language_server_windows_x64.exe
```

当前不替换该 language server，只做 Environment/settings 方案：

- 生成 `Antigravity IDE-Proxy.lnk`
- 注入 Chromium 参数和代理环境变量
- 写入 `%APPDATA%\Antigravity IDE\User\settings.json`

Antigravity IDE 不使用 Antigravity 主应用的 `127.0.0.1:18990` relay，也不替换 language server。它可以与 Antigravity 同时运行；两者的共同影响主要是共享同一个本地代理出口和同一个账号的 CloudCode 并发请求。

## 关键文件

- `AppProxyHelper/AppProxyConfig.cs`：配置模型。
- `AppProxyHelper/ConfigLoader.cs`：配置加载、保存、验证和 Chromium 参数整理。
- `AppProxyHelper/ProcessProxyLauncher.cs`：目标进程启动和环境变量注入。
- `AppProxyHelper/ProxyEnvironmentProfile.cs`：统一生成 Chromium 参数、HTTP(S)/gRPC 代理环境变量和 no-proxy 变量。
- `AppProxyHelper/KnownEditorProxySettings.cs`：同步 Cursor、Antigravity、Antigravity IDE 的 VS Code 风格代理设置。
- `AppProxyHelper/ProxyLauncherScriptGenerator.cs`：生成应用目录脚本和 `*-Proxy` 快捷方式。
- `AppProxyHelper/TargetApplicationCatalog.cs`：自动发现 Codex、Cursor、Antigravity、Antigravity IDE。
- `AppProxyHelper/AntigravityCloudCodeRelay.cs`：Antigravity CloudCode 本机 relay。
- `AppProxyHelper/AntigravityLanguageServerShim.cs`：Antigravity language server shim。
- `AppProxyHelper/AppProxyGui.cs`：WinForms GUI。
- `AppProxyHelper/Cli.cs`：命令行入口。
- `app-proxy.example.json`：Environment-first 示例配置。

Transparent 相关实现代码暂时留在仓库中作为历史实现，但不再由配置、GUI、CLI 或打包流程作为主入口使用。

## 维护注意事项

- 生成代理图标时必须保留原图标，新增 `应用名-Proxy.lnk`。
- 生成脚本默认进入应用目录的 `EasyProxy` 子目录。
- Antigravity 新版的 shim 和 relay 是特化处理；不要套到 Antigravity IDE。
- Antigravity 的 `onboardUser` 超时通常是 CloudCode 上游或本地代理短时慢响应，不等同于账号风控；优先查看 relay 脱敏日志和重新生成 `Antigravity-Proxy`。
- 如果目标应用更新后代理失效，优先重新生成对应 `*-Proxy` 入口。
- 如果新增应用支持，优先判断它是只需要 Environment/settings，还是需要像 Antigravity 一样做协议 relay 或进程 shim。
