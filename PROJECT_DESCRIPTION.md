# EasyProxy 项目说明

EasyProxy 当前定位为 Electron/Chromium 桌面 AI 应用的代理启动器，优先服务 Codex、Cursor、Antigravity 和 Antigravity IDE。

## 产品方向

早期 Transparent 方案依赖 WinDivert 做系统级透明拦截。实际验证中，Codex 和 Antigravity 在关闭全局 TUN 后会遇到首包慢、重连、QUIC/UDP 绕行、WinDivert 驱动兼容等问题。对当前目标应用来说，Transparent 没有比显式 Chromium 代理参数和环境变量更稳定。

现在产品入口收敛为：

1. 固定使用 `Environment` 方案。
2. 由 EasyProxy 或生成的代理脚本启动目标应用。
3. 注入 Chromium 代理参数和代理环境变量。
4. 为已知编辑器同步 VS Code 风格用户 settings。
5. 生成独立 `应用名-Proxy.lnk`，不覆盖原应用图标。
6. 脚本进入应用目录下的 `EasyProxy` 子目录，不堆在桌面。

## 应用处理策略

### Codex

Codex 使用 Chromium 参数和环境变量。由于 Microsoft Store/WindowsApps 目录通常不可写，代理脚本放到：

```text
%LOCALAPPDATA%\Packages\OpenAI.Codex_2p2nqsd0c76g0\LocalCache\EasyProxy
```

Codex 的 Microsoft Store 包目录会在每次更新后带上新的版本号，例如：

```text
C:\Program Files\WindowsApps\OpenAI.Codex_26.519.2736.0_x64__2p2nqsd0c76g0
```

因此自动发现不能硬编码完整版本路径。当前策略是优先通过 `Get-AppxPackage -Name OpenAI.Codex` 读取 `InstallLocation`，因为普通权限下直接枚举 `C:\Program Files\WindowsApps` 可能失败。若 AppX 查询不可用，再回退到枚举：

```text
C:\Program Files\WindowsApps\OpenAI.Codex_*__2p2nqsd0c76g0
```

回退枚举会用正则从目录名提取版本号，按版本号倒序选择最新包，并拼接 `app\Codex.exe`。这样 Codex 更新后重新生成 `Codex-Proxy` 时，EasyProxy 会优先找到最新安装目录。

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
  -> Node CloudCode relay on 127.0.0.1:18990 from %LOCALAPPDATA%\EasyProxy\AntigravityRelay
  -> Antigravity.exe
  -> EasyProxy language_server.exe shim
  -> language_server.easyproxy-original.exe
```

shim 会把 CloudCode endpoint 改到本机 relay，relay 再通过用户代理访问 Google CloudCode/Google APIs。relay 保留脱敏日志 `Antigravity CloudCode Relay.log`，用于排查请求是否到达上游、耗时和 HTTP 状态。relay 脚本和日志位于 `%LOCALAPPDATA%\EasyProxy\AntigravityRelay`，避免 Antigravity 更新时锁住安装目录。启动阶段的 `v1internal:onboardUser` 偶尔会因为上游响应超过 Antigravity 的短超时而失败，relay 会对该接口做备用 upstream 重试和短期缓存。

应用更新可能覆盖 shim。恢复方式是重新运行 EasyProxy 的脚本生成功能。

### Antigravity IDE

Antigravity IDE 的 language server 位于：

```text
resources\app\extensions\antigravity\bin\language_server_windows_x64.exe
```

实测该 server 对普通 `HTTP_PROXY`/`HTTPS_PROXY` 环境变量不稳定：CloudCode endpoint 可以通过 settings 改到本机，但 OAuth userinfo/tokeninfo 等硬编码 Google APIs 仍可能直连并超时。当前策略是生成一个旁路 patched 副本，不覆盖原始 server：

```text
language_server_windows_x64.exe
language_server_windows_x64.easyproxy-pristine.exe
language_server_windows_x64.easyproxy-patched.exe
```

链路：

```text
Antigravity IDE-Proxy.lnk
  -> AntigravityIDEProxy.vbs
  -> AntigravityIDEProxy.cmd
  -> Node CloudCode relay on 127.0.0.1:18990 from %LOCALAPPDATA%\EasyProxy\AntigravityRelay
  -> Antigravity IDE.exe
  -> language_server_windows_x64.easyproxy-patched.exe
```

settings 写入：

- `jetski.cloudCodeUrl = http://127.0.0.1:18990`
- `codeiumDev.languageServerBinaryPath`
- `codeiumDev.machineLanguageServerBinaryPath`
- `codeiumDev.languageServerEnv`
- `http.proxy` 等基础代理项

patched 副本会把 CloudCode、OAuth userinfo/tokeninfo、Drive/Upload 相关硬编码请求导向本机 relay。Antigravity 与 Antigravity IDE 可同时运行，并复用同一个健康的 `127.0.0.1:18990` relay。

## 关键文件

- `AppProxyHelper/AppProxyConfig.cs`：配置模型。
- `AppProxyHelper/ConfigLoader.cs`：配置加载、保存、验证和 Chromium 参数整理。
- `AppProxyHelper/ProcessProxyLauncher.cs`：目标进程启动和环境变量注入。
- `AppProxyHelper/ProxyEnvironmentProfile.cs`：统一生成 Chromium 参数、HTTP(S)/gRPC 代理环境变量和 no-proxy 变量。
- `AppProxyHelper/KnownEditorProxySettings.cs`：同步 Cursor、Antigravity、Antigravity IDE 的 VS Code 风格代理设置。
- `AppProxyHelper/ProxyLauncherScriptGenerator.cs`：生成应用目录脚本和 `*-Proxy` 快捷方式。
- `AppProxyHelper/TargetApplicationCatalog.cs`：自动发现 Codex、Cursor、Antigravity、Antigravity IDE。
- `AppProxyHelper/AntigravityCloudCodeRelay.cs`：Antigravity 系列 CloudCode 本机 relay。
- `AppProxyHelper/AntigravityLanguageServerShim.cs`：Antigravity 主应用 shim 与 Antigravity IDE patched server 副本生成。
- `AppProxyHelper/AppProxyGui.cs`：WinForms GUI。
- `AppProxyHelper/Cli.cs`：命令行入口。
- `app-proxy.example.json`：Environment-first 示例配置。

Transparent 相关实现暂时留在仓库中作为历史实现，但不再由配置、GUI、CLI 或打包流程作为主入口使用。

## 维护注意事项

- 生成代理图标时必须保留原图标，只新增 `应用名-Proxy.lnk`。
- 生成脚本默认进入应用目录的 `EasyProxy` 子目录。
- Codex 的 WindowsApps 安装目录包含滚动版本号；不要新增硬编码版本路径，优先维护 `TargetApplicationCatalog` 中的 AppX 查询、包名前缀、publisher id 和版本排序逻辑。
- Antigravity 主应用使用 shim；Antigravity IDE 使用 patched language server 副本，不要混用。
- Antigravity 系列共用 `127.0.0.1:18990` relay；健康 relay 可复用，只有不健康或旧脚本才重启。
- 应用更新后代理失效时，优先重新生成对应 `*-Proxy` 入口。
- 如果新增应用支持，优先判断它只需要 Environment/settings，还是需要类似 Antigravity 的协议 relay 或进程 shim。
