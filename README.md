# EasyProxy

EasyProxy 是面向桌面 AI 应用的代理启动器，当前默认方案是 `Environment`：由 EasyProxy 或生成的 `*-Proxy` 启动脚本启动目标应用，并注入 Chromium 代理参数、代理环境变量和应用自身 settings。旧的 Transparent/WinDivert 透明拦截不再作为日常入口。

## 支持应用

| 应用 | 入口 | 代理方式 |
| --- | --- | --- |
| Codex | `Codex-Proxy` | Chromium 参数 + 环境变量 |
| Cursor | `Cursor-Proxy` | Chromium 参数 + 环境变量 + VS Code 风格 settings |
| Antigravity | `Antigravity-Proxy` | Chromium 参数 + 环境变量 + CloudCode relay + language server shim |
| Antigravity IDE | `Antigravity IDE-Proxy` | Chromium 参数 + 环境变量 + CloudCode relay + patched language server 副本 |

原始应用图标不会被修改。比如 `Codex` 仍然直连启动原应用，`Codex-Proxy` 才是代理入口。

## 启动链路

生成代理入口后，桌面和开始菜单会出现新的 `*-Proxy` 快捷方式：

```text
*-Proxy.lnk
  -> 应用目录\EasyProxy\*.vbs
  -> 应用目录\EasyProxy\*.cmd
  -> 目标应用 exe
```

`.vbs` 负责隐藏命令行窗口，`.cmd` 负责设置环境变量、启动本机 relay，并带代理参数启动应用。

脚本默认放在应用自己的可写目录：

- Antigravity: `%LOCALAPPDATA%\Programs\Antigravity\EasyProxy`
- Antigravity IDE: `%LOCALAPPDATA%\Programs\Antigravity IDE\EasyProxy`
- Cursor: `%LOCALAPPDATA%\Programs\cursor\EasyProxy`
- Codex: `%LOCALAPPDATA%\Packages\OpenAI.Codex_2p2nqsd0c76g0\LocalCache\EasyProxy`

生成 `CodexProxy.cmd` 时，如果检测到 `D:\CodeSpace\GamerUnit_Main\GamerUnitManagement\start-dbhub.bat`，会在启动 Codex 前静默调用它，用于确保本机 DBHub MCP 服务已经监听。

## 代理参数

EasyProxy 会自动注入：

- `--disable-quic`
- `--proxy-server=<proxyUri>`
- `--proxy-bypass-list=localhost;127.0.0.1;::1;<local>`
- `HTTP_PROXY` / `HTTPS_PROXY` / `ALL_PROXY`
- `http_proxy` / `https_proxy` / `all_proxy`
- `GRPC_PROXY` / `grpc_proxy`
- `NO_PROXY` / `NO_GRPC_PROXY`

输入 `socks5://127.0.0.1:7890` 时，Chromium 参数仍使用 SOCKS5；后端 language server、gRPC 和 relay 会优先使用更兼容的 `http://127.0.0.1:7890`。

## Antigravity

新版 Antigravity 需要额外处理 CloudCode 请求。EasyProxy 会：

- 生成 `Antigravity-Proxy` 快捷方式。
- 在 `%LOCALAPPDATA%\EasyProxy\AntigravityRelay` 生成并启动 `Antigravity CloudCode Relay.js`，避免 Antigravity 更新时锁住安装目录。
- relay 默认监听 `http://127.0.0.1:18990`。
- relay 写入脱敏诊断日志 `Antigravity CloudCode Relay.log`。
- relay 对启动阶段容易超时的 `v1internal:onboardUser` 做备用 upstream 重试和短期缓存。
- 如果没有 Node.js，启动脚本会尝试用 `winget` 安装 Node.js LTS。
- 安装 language server shim：
  - 原始文件备份为 `language_server.easyproxy-pristine.exe`
  - 打补丁后的原始服务为 `language_server.easyproxy-original.exe`
  - `language_server.exe` 替换为 EasyProxy shim

应用更新后，Antigravity 可能覆盖 `resources\bin\language_server.exe`，重新运行脚本生成功能即可恢复。

## Antigravity IDE

Antigravity IDE 的 server 实测不会稳定读取普通代理环境变量；只写 `HTTP_PROXY` 会出现 server 直连 Google、超时并崩溃。因此 EasyProxy 现在也让 Antigravity IDE 使用本机 relay：

```text
Antigravity IDE-Proxy.lnk
  -> AntigravityIDEProxy.vbs
  -> AntigravityIDEProxy.cmd
  -> Node CloudCode relay on 127.0.0.1:18990
  -> Antigravity IDE.exe
  -> language_server_windows_x64.easyproxy-patched.exe
```

EasyProxy 不覆盖 IDE 原始 `language_server_windows_x64.exe`，而是创建旁路副本：

- `language_server_windows_x64.easyproxy-pristine.exe`
- `language_server_windows_x64.easyproxy-patched.exe`

settings 会写入：

- `http.proxy`
- `http.proxySupport`
- `http.systemCertificates`
- `jetski.cloudCodeUrl = http://127.0.0.1:18990`
- `codeiumDev.languageServerEnv`
- `codeiumDev.languageServerBinaryPath`
- `codeiumDev.machineLanguageServerBinaryPath`

patched 副本会把 CloudCode、OAuth userinfo/tokeninfo、Drive/Upload 相关硬编码请求导向本机 relay。Antigravity 与 Antigravity IDE 可以同时运行，并共用同一个健康的 `127.0.0.1:18990` relay。relay 脚本和日志位于 `%LOCALAPPDATA%\EasyProxy\AntigravityRelay`，不放在 Antigravity IDE 安装目录内。

## 生成代理入口

GUI 中使用“生成代理启动脚本”即可。命令行也可以：

```powershell
EasyProxy scripts --app all --proxy socks5://127.0.0.1:7890
EasyProxy scripts --app codex --proxy socks5://127.0.0.1:7890
EasyProxy scripts --app cursor --proxy socks5://127.0.0.1:7890
EasyProxy scripts --app antigravity --proxy socks5://127.0.0.1:7890
EasyProxy scripts --app antigravity-ide --proxy socks5://127.0.0.1:7890
```

应用更新、安装路径变化、桌面图标丢失或代理失效时，重新生成一次对应 `*-Proxy` 即可。

## 配置运行

也可以用配置文件直接运行目标应用：

```powershell
EasyProxy init --config app-proxy.json
EasyProxy check --config app-proxy.json
EasyProxy run --config app-proxy.json
EasyProxy ui --config app-proxy.json
```

完整示例见 `app-proxy.example.json`。

## 常见排查

- 点原应用图标不会走代理；请点 `*-Proxy` 图标。
- Antigravity 或 Antigravity IDE 更新后失效，先重新生成对应 `*-Proxy`。
- `127.0.0.1:18990` 被非 EasyProxy 进程占用时，Antigravity 系列 relay 无法启动。
- relay 日志出现相关请求并返回 200，说明账号和代理链路基本正常；UI 仍卡住时优先重载窗口或完整重启对应应用。
- 没有 Node.js 时脚本会尝试 `winget` 安装；没有 `winget` 时需要手动安装 Node.js LTS。

## 打包

```powershell
.\build-exe.bat win-x64 Release
.\build-msi.bat
.\build-setup.bat
```

## Codex QQ Skin

GUI 的“AI 应用代理生成”区域提供独立的 `Codex 启用 QQ Skin` 勾选项。该选项写入
`app-proxy.json` 的 `enableCodexQqSkin` 字段：

```json
{
  "enableCodexQqSkin": true
}
```

启用后，`Codex-Proxy` 会在同一次启动中注入代理参数与本机 CDP 参数，等待 CDP
端点可用后再调用已安装的 Codex QQ Skin。QQ Skin 未安装或安装目录被移除时，
启动器会自动回退为仅代理模式，不阻止 Codex 启动。

命令行也可显式控制：

```powershell
EasyProxy scripts --app codex --proxy socks5://127.0.0.1:7890 --codex-qq-skin
EasyProxy scripts --app codex --proxy socks5://127.0.0.1:7890 --no-codex-qq-skin
```

发布目录只包含 EasyProxy 主程序和示例配置，不再包含 WinDivert 驱动文件。
