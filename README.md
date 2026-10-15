# EasyProxy

EasyProxy 是一个面向 Windows 桌面应用的代理工具。它为 ChatGPT、Cursor、Claude、Antigravity 和 Steam 等应用统一生成独立的 `*-Proxy` 启动入口。Electron/Chromium 应用使用代理参数、环境变量和应用设置；Steam 入口会先启动本地 HTTPS 反向代理，再启动 Steam。

AI 应用代理当前只使用 `Environment` 模式。历史 Transparent/WinDivert 实现仍保留在源码中，但不再出现在配置、GUI、CLI 或发布包的日常入口里。

## 功能概览

- 自动发现已安装的 ChatGPT、Cursor、Claude、Antigravity、Antigravity IDE 和 Steam。
- 为每个应用新增独立的 `应用名-Proxy` 桌面及开始菜单快捷方式，不覆盖原应用图标。
- 注入 `--disable-quic`、`--proxy-server`、`--proxy-bypass-list` 和常用代理环境变量。
- 自动同步 Cursor、Antigravity、Antigravity IDE 的 VS Code 风格代理设置。
- 为 ChatGPT 持久化后端所需的 `%CODEX_HOME%\.env` 代理变量，并保留其中无关配置。
- 为 Claude Desktop 使用 HTTP 兼容代理参数、维护 Claude Code 环境设置，并在需要时启动 `CoworkVMService`。
- 为 Antigravity 系列提供本机 CloudCode relay，以及 language server shim/旁路 patched 副本。
- 在资源管理器中安装“在此处打开 Claude/Codex”的代理右键菜单。
- 提供代理握手、CONNECT 和 TLS 诊断，以及 GUI 动态日志。
- 将 Steam 纳入相同的应用勾选与一键生成流程，生成 `Steam-Proxy` 快捷方式，为商店、社区、API 和网页资源提供 Hosts 接入、本地 TLS 终结与上游代理回源。

## 支持的应用

| 应用 | `--app` 值 | 生成入口 | 额外处理 |
| --- | --- | --- | --- |
| ChatGPT Desktop | `chatgpt` | `ChatGPT-Proxy` | 动态解析最新 `OpenAI.Codex` MSIX；写入 `%CODEX_HOME%\.env` |
| Cursor | `cursor` | `Cursor-Proxy` | 同步 `%APPDATA%\Cursor\User\settings.json` |
| Claude Desktop | `claude` | `Claude-Proxy` | 使用 HTTP 兼容代理；支持 MSIX 激活与 `CoworkVMService` |
| Antigravity | `antigravity` | `Antigravity-Proxy` | CloudCode relay + language server shim |
| Antigravity IDE | `antigravity-ide` | `Antigravity IDE-Proxy` | CloudCode relay + patched language server 副本 |
| Steam | `steam` | `Steam-Proxy` | 先启动本地 HTTPS 反向代理，再启动 Steam；首次安装当前用户 CA 并保留复用，退出后停止后台并恢复 Hosts |
| 其他 Electron/Chromium 应用 | 自定义 `--target` | `<名称>-Proxy` | Chromium 参数 + 代理环境变量 |

`codex` 仍可作为 `chatgpt` 的兼容别名。ChatGPT 的 Microsoft Store 技术包名目前仍是 `OpenAI.Codex`，这类兼容名称不会被改写。

## 快速开始

### 使用 GUI

直接运行 `EasyProxy.exe`。不带参数时程序会打开 WinForms 界面：

1. 在“代理地址”中填写代理，例如 `socks5://127.0.0.1:7890`。
2. 点击“检查代理”确认本机端口、代理握手和 TLS 链路。
3. 在应用代理生成列表中勾选已检测到的应用，Steam 与其他应用使用相同操作。
4. 点击“一键生成代理启动脚本”。
5. 以后从桌面或开始菜单运行新的 `*-Proxy` 快捷方式。

原应用快捷方式仍保持原样。Steam 代理会话期间，系统访问其托管域名都会经过本地代理；Steam 主进程退出后自动恢复，也可执行 `EasyProxy steam stop` 提前停止。

勾选 Steam 后会生成 `SteamProxy.cmd`、`SteamProxy.vbs` 和 `Steam-Proxy` 快捷方式。生成阶段只准备启动入口；运行快捷方式时才首次安装或复用已安装的 CA、请求 UAC 并启动 Steam 代理会话，详见下方“Steam 网页代理”。

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
EasyProxy scripts --app steam --proxy socks5://127.0.0.1:7890

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
- Electron/Chromium 应用的 `.cmd` 设置代理环境变量、准备应用专用组件，并启动目标应用。
- Steam 的 `SteamProxy.cmd` 调用 EasyProxy 的 Steam 启动流程，等待 HTTPS 代理就绪后再启动 Steam；它通过 `SteamProxy.vbs` 隐藏命令行窗口，并由 `Steam-Proxy.lnk` 引用。
- ChatGPT 和 Claude 的 MSIX 入口还会生成 `Start-PackagedApp.ps1`，在 WindowsApps 禁止直接启动 exe 时回退到包激活。

脚本默认写入目标应用目录下的 `EasyProxy` 子目录。对于 WindowsApps/MSIX 应用，脚本写入对应包的可写 `LocalCache\EasyProxy`；无法使用目标目录时，回退到 `%LOCALAPPDATA%\EasyProxy`。

Steam 默认生成到安装目录的 `EasyProxy` 子目录；该目录不可写时回退到 `%LOCALAPPDATA%\EasyProxy\Steam`。显式指定的 `--out` 目录写入失败时会报错。

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

Relay 使用专用运行程序 `EasyProxyRelayHost.exe`，避免被按 `node` 名称清理进程的操作误杀。EasyProxy 从本机 Node.js 复制运行程序到 `AntigravityRelay\runtime\<SHA256>`，Node.js 更新后使用新的目录；启动时会把已确认的旧 Node Relay 切换到专用运行程序。升级 EasyProxy 后需更新已有代理启动入口。

- Antigravity：备份原始 language server，并安装由 EasyProxy 主程序承载的 shim。
- Antigravity IDE：不覆盖原始 `language_server_windows_x64.exe`，而是生成 `.easyproxy-pristine.exe` 和 `.easyproxy-patched.exe` 旁路副本，并通过 settings 指向 patched 副本。
- 如果没有 Node.js，启动脚本会尝试通过 `winget` 安装 Node.js LTS；没有 `winget` 时需要手动安装。
- 应用更新可能覆盖 shim 或替换 server，重新生成 `*-Proxy` 即可修复。

## Steam 网页代理

此功能根据 [`Watt Toolkit Steam 加速实现分析报告`](docs/Watt%20Toolkit%20Steam%20加速实现分析报告.md) 实现 Hosts 模式的基础链路：

```text
Steam / 浏览器
  -> Hosts 中的 EasyProxy Steam 域名映射
  -> 127.0.0.1:80 / 127.0.0.1:443（按 SNI 签发证书）
  -> 用户指定的 HTTP(S) / SOCKS5 上游代理，或独立 DNS 解析后直连
  -> Steam 商店 / 社区 / API / 网页资源
```

在首页应用列表勾选 Steam，填写与其他应用共用的代理地址，再点击“一键生成代理启动脚本”。生成的 `Steam-Proxy.lnk` 经 `SteamProxy.vbs` / `SteamProxy.cmd` 调用 EasyProxy：为本次会话启动 Steam 代理，确认就绪后再启动 Steam。生成脚本和快捷方式时不会修改系统 Hosts 或安装根证书。

运行 `Steam-Proxy` 时，EasyProxy 会在首次使用时安装自己的**当前用户** CA 信任，后续启动复用已安装的证书，并通过 UAC 启动后台进程、写入专用 Hosts 标记区。证书和信任在 Steam 退出后保留，不随每次会话安装、移除。提权必须使用同一 Windows 用户，不支持在 UAC 中切换其他管理员账户。需要空闲的本机 TCP 80 / 443 端口；与 Watt Toolkit 等同端口工具同时运行会发生冲突。

同一 Steam 安装路径下的 `Steam.exe` 主进程全部退出后，EasyProxy 自动停止本次代理后台、恢复 Hosts，并删除临时的 `session-settings.dat`。已安装的 CA、证书私钥和信任保留供下次使用；原有配置、无关 Hosts 条目、诊断日志、生成的脚本和快捷方式也保留。Steam 已经运行或通过单例进程转交启动参数时，会等待实际主进程退出；更新造成的短暂重启保留约 4 秒宽限期，不把 `steamwebhelper.exe` 当作主进程。

```powershell
# 生成 Steam-Proxy 入口；以后运行桌面或开始菜单中的快捷方式
EasyProxy scripts --app steam --proxy socks5://127.0.0.1:7890

# 手动维护后台代理，需自己执行 steam stop；不启动 Steam 客户端
EasyProxy steam start --proxy socks5://127.0.0.1:7890

# 或使用独立 DNS 避开 Hosts 自环后直接回源
EasyProxy steam start --direct

# 查看后台进程、证书信任、Hosts 和日志位置
EasyProxy steam status

# 停止后台代理，并恢复 EasyProxy 管理的 Hosts 映射
EasyProxy steam stop

# 可单独管理当前用户的 CA 信任（移除前先停止）
EasyProxy steam install-cert
EasyProxy steam remove-cert
```

`Steam-Proxy` 使用生成入口时选择的代理地址作为本次会话参数，不将其写成长期 Steam 配置；修改后重新生成入口即可。右键首页的 Steam 勾选项可“查看代理状态”或“停止并恢复”。直接使用 `steam start` 属于手动维护模式，不随客户端退出而停止；不带 `--proxy` 或 `--direct` 时沿用已保存的配置，手动切换上游前先执行 `steam stop`。

Steam 数据与诊断日志位于 `%LOCALAPPDATA%\EasyProxy\SteamProxy`，`steam status` 显示实际路径。`Steam-Proxy` 在会话结束时停止后台、恢复 Hosts 并删除临时会话配置，保留 CA 及其信任。只有用户明确执行 `steam remove-cert` 时才移除当前用户的 CA 信任，执行前需先停止代理。异常退出后可再次执行 `steam stop` 恢复残留映射。

更新或卸载 EasyProxy 前先结束 Steam 代理会话，或执行 `steam stop`；如需同时移除 CA 信任，再执行 `steam remove-cert`。仅关闭 EasyProxy 主窗口不影响正在运行的 Steam 代理会话。

此版本覆盖报告中的 16 个固定网页域名，保留 HTTP 跳转、动态叶子证书和流式 HTTP 转发。它不包含 Watt Toolkit 的远程规则订阅、伪装 SNI 策略、讨论修复脚本、HTML 注入或 `local.steampp.net` 跨域桥；也不处理 Steam 游戏下载、UDP、联机或游戏加速。直连效果取决于实际网络与 DNS，指定上游时取决于该代理能否访问对应 Steam 域名。

实现使用 .NET 10 Kestrel 和 YARP 2.3.0。回源保留原域名、Cookie、压缩和重定向，支持流式请求/响应及 WebSocket；上游 TLS 证书按正常信任链校验。直连通过独立 DoH 查询 IPv4 地址，不回退到系统 Hosts。临时会话配置和 CA 私钥以当前用户 DPAPI 加密；会话结束仅删除临时配置，CA 私钥保留复用。日志不记录请求路径、Cookie 或认证头。

2026-10-15 初版反向代理验证：35 项测试通过，包含本地 HTTPS POST、重定向、多 Cookie、WebSocket、HTTP CONNECT / SOCKS5 及不受信上游证书拒绝。使用临时高端口和私有测试信任链，经本机 `127.0.0.1:7890` 的 HTTP 和 SOCKS5 代理分别访问商店、社区、API 的 `/robots.txt`，6 次均返回 200；直连仅 API 返回 200，商店/社区返回 502，因此当前网络建议指定上游代理。这轮测试未写系统 Hosts 或安装根证书。后续一轮已完成实际 Steam 客户端启动，以系统默认信任访问商店、社区和 API 的 HTTPS 均返回 200，正常关闭 Steam 后 Hosts 自动恢复。

保留 CA 的流程已连续完成两轮实际 Steam 启动及 `Steam.exe -shutdown` 正常退出验证：第二轮复用同一 CA 指纹，公钥证书和 DPAPI 私钥文件哈希均未变化；每轮退出后 Hosts 字节和属性恢复到启动前，80/443 释放，后台与临时会话配置清理完成。最终只保留 CA 公钥、DPAPI 私钥和诊断日志，没有写入永久的 `settings.dat`。第一轮经本地代理访问商店、社区和 API 的 HTTPS 均在默认系统信任下返回 200，第二轮也观察到实际 Steam 请求经代理返回 200。主程序编译为 0 警告、0 错误。

本机测试 DLL 重编译后仍被火绒隔离，告警未消除，也未经厂商确认误报；未关闭防护、添加排除或恢复隔离文件。完整回归测试由 `.github/workflows/windows-tests.yml` 在标准 Windows CI 中执行，包含 C# 代理、证书、Hosts、生命周期及控制通道用例，以及 Relay 测试；测试程序集不包含在安装包内。Windows curl 的严格撤销检查另报 `CRYPT_E_NO_REVOCATION_CHECK`，本地证书暂未提供 CRL；上述 HTTPS 成功记录来自默认信任设置的 .NET 请求及实际 Steam 流量，不代表所有客户端的证书策略均已验证。

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
