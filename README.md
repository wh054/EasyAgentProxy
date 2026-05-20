# EasyProxy

EasyProxy 是一个面向桌面 AI 应用的代理启动器，主要服务 Codex、Cursor、Antigravity 和 Antigravity IDE。

当前默认方案是 `Environment`：由 EasyProxy 或生成的代理启动脚本启动目标应用，并注入 Chromium 启动参数、代理环境变量，以及部分应用自己的用户设置。旧的 Transparent/WinDivert 透明拦截不再作为日常入口使用。

## 支持的应用

| 应用 | 入口 | 代理方式 |
| --- | --- | --- |
| Codex | `Codex-Proxy` | Chromium 参数 + 环境变量 |
| Cursor | `Cursor-Proxy` | Chromium 参数 + 环境变量 + VS Code 风格 settings |
| Antigravity | `Antigravity-Proxy` | Chromium 参数 + 环境变量 + CloudCode relay + language server shim |
| Antigravity IDE | `Antigravity IDE-Proxy` | Chromium 参数 + 环境变量 + VS Code 风格 settings |

原始应用图标不会被修改。例如 `Codex` 仍然直连启动原应用，`Codex-Proxy` 才是代理启动入口。

## 启动链路

生成代理入口后，桌面和开始菜单会出现一个新的 `*-Proxy` 快捷方式：

```text
*-Proxy.lnk
  -> 应用目录\EasyProxy\*.vbs
  -> 应用目录\EasyProxy\*.cmd
  -> 目标应用 exe
```

`.vbs` 只负责隐藏命令行窗口；`.cmd` 负责设置环境变量并带代理参数启动应用。

脚本默认放在应用自己的可写目录：

- Antigravity: `%LOCALAPPDATA%\Programs\Antigravity\EasyProxy`
- Antigravity IDE: `%LOCALAPPDATA%\Programs\Antigravity IDE\EasyProxy`
- Cursor: `%LOCALAPPDATA%\Programs\cursor\EasyProxy`
- Codex: `%LOCALAPPDATA%\Packages\OpenAI.Codex_2p2nqsd0c76g0\LocalCache\EasyProxy`

## 代理参数

EasyProxy 会自动注入：

- `--disable-quic`
- `--proxy-server=<proxyUri>`
- `--proxy-bypass-list=localhost;127.0.0.1;::1;<local>`
- `HTTP_PROXY` / `HTTPS_PROXY` / `ALL_PROXY`
- `http_proxy` / `https_proxy` / `all_proxy`
- `GRPC_PROXY` / `grpc_proxy`
- `NO_PROXY` / `NO_GRPC_PROXY`

当输入 `socks5://127.0.0.1:7890` 时，Chromium 参数仍使用 SOCKS5；后台 language server 和 gRPC 相关环境变量会优先使用兼容性更好的 `http://127.0.0.1:7890`。

## Antigravity 两个版本

### Antigravity

新版 Antigravity 需要额外处理 CloudCode 请求。EasyProxy 会：

- 生成 `Antigravity-Proxy` 快捷方式。
- 在脚本目录生成并启动 `Antigravity CloudCode Relay.js`。
- relay 默认监听 `http://127.0.0.1:18990`。
- relay 会写入脱敏诊断日志 `Antigravity CloudCode Relay.log`，用于确认 CloudCode 请求是否到达上游。
- relay 会清理 hop-by-hop 请求头，并对启动阶段容易超时的 `v1internal:onboardUser` 做一次备用 upstream 重试。
- 如果没有 Node.js，启动脚本会尝试用 `winget` 安装 Node.js LTS。
- 安装 language server shim：
  - 原始文件备份为 `language_server.easyproxy-pristine.exe`
  - 打补丁后的原始服务为 `language_server.easyproxy-original.exe`
  - `language_server.exe` 替换为 EasyProxy shim

应用更新后，Antigravity 可能覆盖 `resources\bin\language_server.exe`，此时重新运行 EasyProxy 的生成脚本功能即可恢复 shim。

### Antigravity IDE

Antigravity IDE 是旧版/IDE 版，结构更接近 VS Code。EasyProxy 不替换它的 language server，只做：

- 生成 `Antigravity IDE-Proxy` 快捷方式。
- 注入 Chromium 代理启动参数。
- 写入 `%APPDATA%\Antigravity IDE\User\settings.json`：
  - `http.proxy`
  - `http.proxySupport`
  - `http.systemCertificates`
  - `codeiumDev.languageServerEnv`

Antigravity 和 Antigravity IDE 可以同时运行。Antigravity 主应用使用本机 relay `127.0.0.1:18990`，Antigravity IDE 不占用这个端口；如果两者同时发起 CloudCode 请求，可能会增加同一个本地代理的连接压力，但这不是账号风控。

## 生成代理入口

GUI 中使用“生成代理启动脚本”即可。命令行也可以：

```powershell
EasyProxy scripts --app all --proxy socks5://127.0.0.1:7890
EasyProxy scripts --app codex --proxy socks5://127.0.0.1:7890
EasyProxy scripts --app cursor --proxy socks5://127.0.0.1:7890
EasyProxy scripts --app antigravity --proxy socks5://127.0.0.1:7890
EasyProxy scripts --app antigravity-ide --proxy socks5://127.0.0.1:7890
```

如果应用更新、安装路径变化、桌面图标丢失，重新生成一次即可。

## 配置运行

也可以用配置文件直接运行目标应用：

```powershell
EasyProxy init --config app-proxy.json
EasyProxy check --config app-proxy.json
EasyProxy run --config app-proxy.json
EasyProxy ui --config app-proxy.json
```

配置示例：

```json
{
  "mode": "Environment",
  "targetPath": "C:\\Path\\To\\Codex.exe",
  "targetArguments": [
    "--disable-quic",
    "--proxy-server=socks5://127.0.0.1:7890",
    "--proxy-bypass-list=localhost;127.0.0.1;::1;<local>"
  ],
  "proxyUri": "socks5://127.0.0.1:7890",
  "injectProxyEnvironment": true,
  "runDiagnosticsBeforeLaunch": true
}
```

完整示例见 `app-proxy.example.json`。

## 常见排查

- 点原应用图标不会走代理；请点 `*-Proxy` 图标。
- Antigravity 更新后对话失败，先重新生成 `Antigravity-Proxy`。
- Antigravity relay 需要本机 `127.0.0.1:18990` 可监听。
- 如果 Antigravity 首次打开出现 `Authentication Required` 或 `onboardUser: context deadline exceeded`，先看 `%LOCALAPPDATA%\Programs\Antigravity\EasyProxy\Antigravity CloudCode Relay.log`：请求返回 200 说明账号和 relay 都正常，多数是上游短时慢响应，重试或重开 `Antigravity-Proxy` 即可恢复。
- 如果 Node.js 不存在，Antigravity 代理脚本会尝试通过 `winget` 安装；没有 `winget` 时需要手动安装 Node.js LTS。
- Cursor 和 Antigravity IDE 如果仍不走代理，检查对应 `%APPDATA%\...\User\settings.json` 是否写入代理设置。

## 打包

```powershell
.\build-exe.bat win-x64 Release
.\build-msi.bat
.\build-setup.bat
```

发布目录只包含 EasyProxy 主程序和示例配置，不再包含 WinDivert 驱动文件。
