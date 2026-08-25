# EasyProxy

EasyProxy 是一个面向 Windows 桌面 AI 应用的代理启动器。它为 ChatGPT、Cursor、Claude、Antigravity 等 Electron/Chromium 应用生成独立的 `*-Proxy` 启动入口，并同时配置 Chromium 代理参数、进程环境变量和应用自身的代理设置。

EasyProxy 当前只使用 `Environment` 模式。历史 Transparent/WinDivert 实现仍保留在源码中，但不再出现在配置、GUI、CLI 或发布包的日常入口里。

## 功能概览

- 自动发现已安装的 ChatGPT、Cursor、Claude、Antigravity 和 Antigravity IDE。
- 为每个应用新增独立的 `应用名-Proxy` 桌面及开始菜单快捷方式，不覆盖原应用图标。
- 注入 `--disable-quic`、`--proxy-server`、`--proxy-bypass-list` 和常用代理环境变量。
- 自动同步 Cursor、Antigravity、Antigravity IDE 的 VS Code 风格代理设置。
- 为 ChatGPT 持久化后端所需的 `%CODEX_HOME%\.env` 代理变量，并保留其中无关配置。
- 为 Claude Desktop 使用 HTTP 兼容代理参数、维护 Claude Code 环境设置，并在需要时启动 `CoworkVMService`。
- 为 Antigravity 系列提供本机 CloudCode relay，以及 language server shim/旁路 patched 副本。
- 在资源管理器中安装“在此处打开 Claude/Codex”的代理右键菜单。
- 提供代理握手、CONNECT 和 TLS 诊断，以及 GUI 动态日志。

## 支持的应用

| 应用 | `--app` 值 | 生成入口 | 额外处理 |
| --- | --- | --- | --- |
| ChatGPT Desktop | `chatgpt` | `ChatGPT-Proxy` | 动态解析最新 `OpenAI.Codex` MSIX；写入 `%CODEX_HOME%\.env` |
| Cursor | `cursor` | `Cursor-Proxy` | 同步 `%APPDATA%\Cursor\User\settings.json` |
| Claude Desktop | `claude` | `Claude-Proxy` | 使用 HTTP 兼容代理；支持 MSIX 激活与 `CoworkVMService` |
| Antigravity | `antigravity` | `Antigravity-Proxy` | CloudCode relay + language server shim |
| Antigravity IDE | `antigravity-ide` | `Antigravity IDE-Proxy` | CloudCode relay + patched language server 副本 |
| 其他 Electron/Chromium 应用 | 自定义 `--target` | `<名称>-Proxy` | Chromium 参数 + 代理环境变量 |

`codex` 仍可作为 `chatgpt` 的兼容别名。ChatGPT 的 Microsoft Store 技术包名目前仍是 `OpenAI.Codex`，这类兼容名称不会被改写。

## 快速开始

### 使用 GUI

直接运行 `EasyProxy.exe`。不带参数时程序会打开 WinForms 界面：

1. 在“代理地址”中填写代理，例如 `socks5://127.0.0.1:7890`。
2. 点击“检查代理”确认本机端口、代理握手和 TLS 链路。
3. 在“AI 应用代理生成”中勾选已检测到的应用。
4. 点击“一键生成代理启动脚本”。
5. 以后从桌面或开始菜单运行新的 `*-Proxy` 快捷方式。

原应用快捷方式仍保持原样；直接点击原图标不会经过 EasyProxy。

### 使用命令行

```powershell
# 为所有已检测到的应用生成入口
EasyProxy scripts --app all --proxy socks5://127.0.0.1:7890

# 只生成一个应用的入口
EasyProxy scripts --app chatgpt --proxy socks5://127.0.0.1:7890
EasyProxy scripts --app cursor --proxy socks5://127.0.0.1:7890
EasyProxy scripts --app claude --proxy socks5://127.0.0.1:7890
EasyProxy scripts --app antigravity --proxy socks5://127.0.0.1:7890
EasyProxy scripts --app antigravity-ide --proxy socks5://127.0.0.1:7890

# 为任意 exe 生成入口
EasyProxy scripts --target "C:\Path\To\App.exe" --name "My App" `
  --proxy socks5://127.0.0.1:7890
```

可以用 `--out DIR`（或 `--output DIR`）指定脚本输出目录。应用更新、安装路径变化、快捷方式丢失或代理设置被覆盖后，重新生成一次对应入口即可。

## 启动链路与生成文件

```text
应用名-Proxy.lnk
  -> EasyProxy\*.vbs
  -> EasyProxy\*.cmd
  -> 目标应用 exe 或 MSIX 激活入口
```

- `.vbs` 隐藏命令行窗口。
- `.cmd` 设置代理环境变量、准备应用专用组件，并启动目标应用。
- ChatGPT 和 Claude 的 MSIX 入口还会生成 `Start-PackagedApp.ps1`，在 WindowsApps 禁止直接启动 exe 时回退到包激活。

脚本默认写入目标应用目录下的 `EasyProxy` 子目录。对于 WindowsApps/MSIX 应用，脚本写入对应包的可写 `LocalCache\EasyProxy`；无法使用目标目录时，回退到 `%LOCALAPPDATA%\EasyProxy`。

ChatGPT 启动脚本还包含一个可选本机钩子：仅当 `D:\CodeSpace\GamerUnit_Main\GamerUnitManagement\start-dbhub.bat` 存在时，才会在启动 ChatGPT 前静默调用它；其他环境不会执行该步骤。

## 代理行为

`proxyUri` 支持以下协议：

- `http://`
- `https://`
- `socks://`
- `socks5://`

EasyProxy 会规范化并注入：

```text
--disable-quic
--proxy-server=<proxyUri>
--proxy-bypass-list=localhost;127.0.0.1;::1;<local>

HTTP_PROXY / HTTPS_PROXY / ALL_PROXY
http_proxy / https_proxy / all_proxy
GRPC_PROXY / grpc_proxy
NO_PROXY / no_proxy
NO_GRPC_PROXY / no_grpc_proxy
```

当输入本机 SOCKS 代理（例如 `socks5://127.0.0.1:7890`）时，Chromium 仍可使用 SOCKS5；对只稳定支持 HTTP 代理的后端、gRPC、Claude 和 Antigravity relay，EasyProxy 会使用同一地址的 `http://127.0.0.1:7890` 兼容形式。因此本机代理程序需要在该端口同时接受对应连接方式。

## 应用专用处理

### ChatGPT Desktop

EasyProxy 优先通过 `Get-AppxPackage -Name OpenAI.Codex` 获取最新安装目录，并保留 WindowsApps 目录枚举作为回退。启动时再次解析最新包，因此 ChatGPT 更新后通常只需重新生成入口，不需要硬编码版本路径。

由于 MSIX 包激活不会继承启动脚本的全部环境变量，EasyProxy 还会把代理变量写入：

```text
%CODEX_HOME%\.env
```

未设置 `CODEX_HOME` 时使用 `%USERPROFILE%\.codex\.env`。EasyProxy 只替换自己管理的代理键，保留文件中的其他配置。

### Cursor

除启动参数和环境变量外，EasyProxy 会合并写入 `%APPDATA%\Cursor\User\settings.json`：

- `http.proxy`
- `http.proxySupport`
- `http.systemCertificates`
- `codeiumDev.languageServerEnv`

### Claude Desktop 与 Claude Code

Claude Desktop 使用 HTTP 兼容的 Chromium 参数和代理环境变量，避免把 SOCKS `ALL_PROXY` 传给不兼容的组件。对于 Store/MSIX 安装，启动器会动态解析最新 `Claude` 包；若存在 `CoworkVMService` 且尚未运行，会尝试启动并等待最多 10 秒。

生成 Claude Desktop 入口时，EasyProxy 会合并维护 `%USERPROFILE%\.claude\settings.json` 的 `env` 对象，并保留无关设置。GUI 的“CLI 右键菜单”还能为 Claude Code CLI 安装“在此处打开 Claude”。

### Antigravity 与 Antigravity IDE

Antigravity 的部分 CloudCode、OAuth 和 Google API 请求不会稳定读取普通代理环境变量，因此 EasyProxy 使用本机 relay：

```text
Antigravity / Antigravity IDE
  -> language server shim 或 patched 副本
  -> http://127.0.0.1:18990
  -> 用户配置的上游代理
```

relay 脚本和脱敏日志位于 `%LOCALAPPDATA%\EasyProxy\AntigravityRelay`。两个应用可以复用同一个健康 relay。

- Antigravity：备份原始 language server，并安装由 EasyProxy 主程序承载的 shim。
- Antigravity IDE：不覆盖原始 `language_server_windows_x64.exe`，而是生成 `.easyproxy-pristine.exe` 和 `.easyproxy-patched.exe` 旁路副本，并通过 settings 指向 patched 副本。
- 如果没有 Node.js，启动脚本会尝试通过 `winget` 安装 Node.js LTS；没有 `winget` 时需要手动安装。
- 应用更新可能覆盖 shim 或替换 server，重新生成 `*-Proxy` 即可修复。

## 配置文件模式

除生成快捷方式外，也可以使用 JSON 配置直接诊断和启动任意目标程序：

```powershell
EasyProxy init --config app-proxy.json
EasyProxy check --config app-proxy.json
EasyProxy run --config app-proxy.json
EasyProxy ui --config app-proxy.json
```

完整示例见 [`app-proxy.example.json`](app-proxy.example.json)。主要字段如下：

| 字段 | 说明 |
| --- | --- |
| `mode` | 只支持 `Environment` |
| `targetPath` | 目标 exe 路径 |
| `targetArguments` | 额外启动参数；代理相关 Chromium 参数会自动规范化 |
| `workingDirectory` | 目标工作目录，可为空 |
| `proxyUri` | HTTP、HTTPS、SOCKS 或 SOCKS5 代理地址 |
| `injectProxyEnvironment` | 是否注入代理环境变量 |
| `runDiagnosticsBeforeLaunch` | 启动前是否运行代理诊断 |
| `abortLaunchWhenDiagnosticsFail` | 诊断失败时是否取消启动 |
| `waitForExit` | 是否等待目标进程退出 |
| `noProxy` | 不经过代理的主机列表 |
| `diagnostics` | 超时、代理握手、CONNECT 和 TLS 测试设置 |

相对日志路径以配置文件所在目录解析。默认日志目录为 `logs`。

## CLI 右键菜单

在 GUI 首页点击“CLI 右键菜单...”，可以为以下工具安装或卸载资源管理器目录右键入口：

- Claude Code CLI：`在此处打开 Claude`
- Codex CLI：`在此处打开 Codex`

生成脚本位于 `%LOCALAPPDATA%\EasyProxy\CliContextMenu`。入口优先使用 Windows Terminal；未安装时回退到 PowerShell，并把所选目录作为工作目录。Claude Code 使用 HTTP 兼容环境变量，Codex CLI 保留完整 HTTP/SOCKS 代理变量。

## 常见问题

- **点开后没有走代理**：确认运行的是 `*-Proxy`，不是原应用图标。
- **找不到应用**：先确认应用已安装，或使用 `--target` 明确指定 exe。
- **应用更新后入口失效**：重新生成该应用的代理入口。
- **代理检查失败**：确认代理进程正在监听、协议与 `proxyUri` 匹配，并检查 GUI 动态日志或配置中的日志目录。
- **Antigravity relay 无法启动**：确认 `127.0.0.1:18990` 没有被其他程序占用，并检查 `%LOCALAPPDATA%\EasyProxy\AntigravityRelay` 下的日志。
- **Antigravity 更新后恢复直连**：重新生成入口以恢复 shim 或 patched language server。
- **Claude/Codex CLI 右键菜单不出现**：在 GUI 中卸载后重新安装，并确认对应 CLI 命令已加入 `PATH`。

## 从源码构建

要求：Windows 10/11 和 .NET 10 SDK。仓库中的脚本会优先使用 `.dotnet\dotnet.exe`，否则使用 `PATH` 中的 `dotnet`。

```powershell
# 运行测试
.\.dotnet\dotnet.exe run --project .\AppProxyHelper.Tests\AppProxyHelper.Tests.csproj -c Release

# 编译项目
.\.dotnet\dotnet.exe build .\AppProxyHelper\AppProxyHelper.csproj -c Release

# 发布 win-x64 自包含单文件程序
.\build-exe.bat win-x64 Release
```

发布结果位于 `dist\EasyProxy-win-x64`，只包含 EasyProxy 主程序和示例配置，不包含 WinDivert 驱动。

### 构建安装包

```powershell
# NSIS .exe 安装器
.\build-setup.bat patch

# WiX MSI 安装包
.\build-msi.bat patch
```

`patch` 也可以替换为 `minor` 或 `major`。安装包版本读取自 `installer\version.txt`，只有构建成功后才会推进；脚本不接受手动版本号。详细规则见 [`installer/README.md`](installer/README.md)。

## 项目结构

```text
AppProxyHelper/                 EasyProxy WinForms 主程序
AppProxyHelper.Tests/           无外部测试框架的发布级冒烟测试
installer/                      WiX / NSIS 安装定义与版本文件
scripts/                        WinDivert、NSIS 和 relay 辅助脚本
app-proxy.example.json          Environment 模式示例配置
PROJECT_DESCRIPTION.md          设计与维护说明
build-exe.bat                   自包含单文件发布
build-msi.bat                   MSI 构建
build-setup.bat                 NSIS 安装器构建
```

更深入的设计和维护约束见 [`PROJECT_DESCRIPTION.md`](PROJECT_DESCRIPTION.md)。
