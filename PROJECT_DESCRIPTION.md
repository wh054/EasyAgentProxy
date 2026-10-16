# EasyProxy 项目说明

EasyProxy 为 Electron/Chromium 桌面 AI 应用和 Steam 提供统一的代理启动入口。应用在同一列表勾选并一键生成 `应用名-Proxy` 快捷方式；Steam 的启动入口负责先准备本地网页反向代理，再启动客户端。所选上游仅用于 Steam 网页，游戏下载和更新保持原链路。

## 产品方向

早期 Transparent 方案依赖 WinDivert 做系统级透明拦截。实际验证中，ChatGPT 和 Antigravity 在关闭全局 TUN 后会遇到首包慢、重连、QUIC/UDP 绕行、WinDivert 驱动兼容等问题。对当前目标应用来说，Transparent 没有比显式 Chromium 代理参数和环境变量更稳定。

现在产品入口收敛为：

1. Electron/Chromium 应用固定使用 `Environment` 方案；Steam 使用自己的 HTTPS 反向代理。
2. 由 EasyProxy 或生成的代理脚本启动目标应用。
3. 为 Electron/Chromium 应用注入 Chromium 代理参数和代理环境变量；Steam 入口等待后台代理就绪后启动客户端。
4. 为已知编辑器同步 VS Code 风格用户 settings。
5. 生成独立 `应用名-Proxy.lnk`，不覆盖原应用图标。
6. 脚本进入应用目录下的 `EasyProxy` 子目录，不堆在桌面。

Steam 纳入首页应用勾选及 `scripts --app steam` / `scripts --app all` 的既有生成流程。其后台服务负责 Hosts 接入、本地 HTTPS 终结、可选上游代理及独立 DNS 回源，不改变 `AppProxyConfig.mode`。生成 Steam 启动入口不会修改 Hosts 或根证书信任；运行 `Steam-Proxy` 时首次安装当前用户 CA，后续复用并保留。Steam 主进程退出后停止后台、恢复 Hosts 并删除临时会话配置。

## 应用处理策略

### ChatGPT

ChatGPT 使用 Chromium 参数和环境变量。由于 Microsoft Store/WindowsApps 目录通常不可写，代理脚本放到：

```text
%LOCALAPPDATA%\Packages\OpenAI.Codex_2p2nqsd0c76g0\LocalCache\EasyProxy
```

ChatGPT 的 Microsoft Store 包仍使用技术包名 `OpenAI.Codex`，目录会在每次更新后带上新的版本号，例如：

```text
C:\Program Files\WindowsApps\OpenAI.Codex_26.519.2736.0_x64__2p2nqsd0c76g0
```

因此自动发现不能硬编码完整版本路径。当前策略是优先通过 `Get-AppxPackage -Name OpenAI.Codex` 读取 `InstallLocation`，因为普通权限下直接枚举 `C:\Program Files\WindowsApps` 可能失败。若 AppX 查询不可用，再回退到枚举：

```text
C:\Program Files\WindowsApps\OpenAI.Codex_*__2p2nqsd0c76g0
```

回退枚举会用正则从目录名提取版本号，按版本号倒序选择最新包，并优先拼接 `app\ChatGPT.exe`。这样 ChatGPT 更新后重新生成代理入口时，EasyProxy 会优先找到最新安装目录。

生成入口为 `ChatGPT-Proxy.lnk`。

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

### Steam 网页代理

实现依据为 `docs/Watt Toolkit Steam 加速实现分析报告.md` 的网页分组。本版本接管商店、社区、帮助、登录、结算、专用图片/静态资源及视频封面，共 12 个明确域名；域名清单见 README 的“Steam 网页代理”。不使用 Steam 或 CDN 后缀通配。

从原 16 域规则中排除 `media.steampowered.com`（更新组）、`steamcdn-a.akamaihd.net` / `cdn.akamai.steamstatic.com`（通用共享 CDN）和 `api.steampowered.com`（包含下载服务器发现）。游戏下载、更新、SteamPipe 与独立 CDN 修复域名不写 Hosts；原有下载链路保持不变。Steam 启动入口不注入通用代理环境变量或设置系统代理，配置的上游仅用于网页白名单。

```text
首页应用列表勾选 Steam（网页） → 一键生成代理启动脚本
  -> Steam-Proxy.lnk → SteamProxy.vbs → SteamProxy.cmd
  -> EasyProxy steam launch（临时会话参数）
  -> 首次安装或复用当前用户 EasyProxy Steam CA 信任
  -> 隐藏、提权的 EasyProxy steam run-session 后台进程
  -> 127.0.0.1:80（HTTPS 跳转） / 127.0.0.1:443（SNI 动态证书）
  -> 专用 Hosts 标记区
  -> 启动 Steam；仅白名单网页经用户上游代理或独立 DNS 直连回源
  -> 游戏下载、更新和下载服务发现沿原链路
  -> 等待同路径 Steam.exe 主进程全部退出（重启宽限约 4 秒）
  -> 结束本次后台，恢复 Hosts，删除临时会话配置；保留 CA 供下次使用
```

Steam 与其他应用共用首页代理地址、应用勾选列表和生成按钮，勾选项显示“Steam（网页）”，提示游戏下载和更新沿原链路，不新增页面或布局。生成文件仍为 `SteamProxy.cmd`、`SteamProxy.vbs` 以及桌面/开始菜单中的 `Steam-Proxy.lnk`。快捷方式调用 `steam launch`，由 EasyProxy 为本次会话准备后台服务，等待就绪后启动 Steam。首次安装或复用 CA、通过同用户 UAC 写 Hosts 均在运行阶段处理，不在生成阶段执行。关闭 GUI 不影响当前 Steam 会话。

`SteamProcessLifetime` 以 Steam.exe 完整路径跟踪主进程：兼容 Steam 已运行、启动器瞬退、单例传参和短暂更新重启，忽略其他安装路径的 Steam 及 `steamwebhelper.exe`。全部主进程连续约 4 秒不存在后，会话停止后台、恢复 Hosts 并删除临时配置；已安装 CA、证书私钥和信任保留供下次复用，原有配置、诊断日志、生成的脚本及快捷方式保留，不终止 Steam 进程。

CLI 保留 `steam start [--proxy URI | --direct]`、`steam stop`、`steam status`、`steam install-cert`、`steam remove-cert`；生成脚本消费 `steam launch`，`steam run-session`、`steam run` 和 `steam recover` 用于内部会话、手动后台与异常恢复。`steam launch` 的上游地址仅用于本次会话，不写成长期 Steam 设置。直接使用 `steam start` 则属于独立的手动维护模式，不会启动客户端，也不随客户端退出而停止；无参数时沿用已保存的 Steam 上游配置，首次默认值与其他应用的代理默认值一致。

后台服务监听成功后才写 Hosts；停止或启动失败时恢复 EasyProxy 自己管理的映射。默认数据目录为 `%LOCALAPPDATA%\EasyProxy\SteamProxy`。会话设置使用临时的 `session-settings.dat`，退出后删除，不改永久的 `settings.dat`。会话与手动维护模式复用 EasyProxy 自己的 CA，证书私钥以当前用户 DPAPI 加密并持久保存，当前用户根证书信任不随会话退出而移除，也不复用 Watt Toolkit 私钥。用户需要移除信任时，在停止代理后手动执行 `steam remove-cert`。

2026-10-15 验证边界：初版 35 项测试已通过；保留 CA 的流程已连续完成两轮实际 Steam 启动与正常退出验证，第二轮复用同一证书和 DPAPI 私钥，退出后 Hosts 字节与属性恢复、80/443 释放、后台和临时会话配置删除。商店、社区和 API 的默认系统信任 HTTPS 请求均返回 200，并观察到实际 Steam 流量经代理返回 200。主程序编译为 0 警告、0 错误。本机测试 DLL 重编译后仍被火绒隔离，告警尚未消除或经厂商复核；完整回归由标准 Windows CI 执行，安装包不包含测试程序集。Windows curl 严格撤销检查因本地证书未提供 CRL 而失败，尚不代表所有客户端证书策略均通过。

上述验证对应范围收敛前的规则，API 当时仍被接管；本次 12 域网页策略和游戏下载隔离的运行时结果须单独验证。

此版本不实现远程规则订阅、伪装 SNI、第三方讨论修复脚本、HTML 注入和跨域桥。Steam 游戏下载、更新、UDP 和游戏联机保持原链路；depot 下载路径不得进入网页上游。固定域名列表和本地验证不能代替 Steam 客户端实际访问的运行时验证。

## 关键文件

- `AppProxyHelper/AppProxyConfig.cs`：配置模型。
- `AppProxyHelper/ConfigLoader.cs`：配置加载、保存、验证和 Chromium 参数整理。
- `AppProxyHelper/ProcessProxyLauncher.cs`：目标进程启动和环境变量注入。
- `AppProxyHelper/ProxyEnvironmentProfile.cs`：统一生成 Chromium 参数、HTTP(S)/gRPC 代理环境变量和 no-proxy 变量。
- `AppProxyHelper/KnownEditorProxySettings.cs`：同步 Cursor、Antigravity、Antigravity IDE 的 VS Code 风格代理设置，并持久化 ChatGPT 后端使用的 `.env` 代理变量。
- `AppProxyHelper/ProxyLauncherScriptGenerator.cs`：生成应用目录脚本和 `*-Proxy` 快捷方式，包含 Steam 后台就绪后启动客户端的专用脚本。
- `AppProxyHelper/TargetApplicationCatalog.cs`：自动发现 ChatGPT、Cursor、Claude、Antigravity、Antigravity IDE 和 Steam。
- `AppProxyHelper/AntigravityCloudCodeRelay.cs`：Antigravity 系列 CloudCode 本机 relay。
- `AppProxyHelper/AntigravityLanguageServerShim.cs`：Antigravity 主应用 shim 与 Antigravity IDE patched server 副本生成。
- `AppProxyHelper/AppProxyGui.cs`：WinForms GUI。
- `AppProxyHelper/Cli.cs`：命令行入口。
- `AppProxyHelper/SteamProxyCli.cs`：Steam 启动入口、后台管理命令、参数验证和状态展示。
- `AppProxyHelper/SteamProxyProcessController.cs`：Steam 后台进程与提权控制。
- `AppProxyHelper/SteamApplicationLauncher.cs`：准备临时代理会话、启动 Steam，并在 Steam 退出后结束会话。
- `AppProxyHelper/SteamProcessLifetime.cs`：跟踪同安装路径的 Steam 主进程并等待单例转交、更新重启与退出。
- `app-proxy.example.json`：Environment-first 示例配置。

Transparent 相关实现暂时留在仓库中作为历史实现，但不再由配置、GUI、CLI 或打包流程作为主入口使用。

## 维护注意事项

- 生成代理图标时必须保留原图标，只新增 `应用名-Proxy.lnk`。
- 生成脚本默认进入应用目录的 `EasyProxy` 子目录。
- ChatGPT 的 WindowsApps 安装目录包含滚动版本号；不要新增硬编码版本路径，优先维护 `TargetApplicationCatalog` 中的 AppX 查询、包名前缀、publisher id 和版本排序逻辑。
- Antigravity 主应用使用 shim；Antigravity IDE 使用 patched language server 副本，不要混用。
- Antigravity 系列共用 `127.0.0.1:18990` relay；健康 relay 可复用，只有不健康或旧脚本才重启。
- 应用更新后代理失效时，优先重新生成对应 `*-Proxy` 入口。
- 如果新增应用支持，优先判断它只需要 Environment/settings，还是需要类似 Antigravity 的协议 relay 或进程 shim。
- Steam Hosts 操作只维护专用标记区，停止和失败恢复必须保留用户及其他工具的 Hosts 内容；验证不得把启动服务、修改系统 Hosts 或安装证书默认为普通单元测试步骤。
- Steam 保持与其他应用相同的勾选、生成、快捷方式入口；生成不得启动后台服务、修改 Hosts 或安装 CA，运行快捷方式时才处理这些操作。
- Steam 规则只允许网页白名单；读取旧配置时迁移并剔除上述四个排除域名，不得直接复用旧策略后台。策略版本与 depot 下载路径保护必须同步维护，禁止把下载/CDN 修复组或下载服务器发现重新并入网页上游。
- `Steam-Proxy` 随同路径 Steam 主进程的生命周期停止后台、恢复 Hosts 并删除临时会话配置；不能仅等待 Process.Start 返回的短命启动器，也不能结束用户的 Steam 进程。
- CA 首次安装后持久保存并复用，退出不移除证书、私钥或信任；仅临时的 `session-settings.dat` 在会话结束时删除，诊断日志、启动脚本、快捷方式及用户原有显式配置保留。
- Steam CA 只由 Steam 功能管理，运行中拒绝移除信任；日志不得记录用户代理凭据、Steam Cookie 和认证头。
