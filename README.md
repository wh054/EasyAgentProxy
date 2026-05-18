# EasyProxy

EasyProxy 是一个 Electron/Chromium 应用代理启动器，主要面向 Codex、Cursor、Antigravity 这类桌面 AI 应用。

它不再提供 Transparent/WinDivert 透明拦截入口。当前策略很直接：由 EasyProxy 启动目标 `.exe`，并自动注入 Chromium 代理启动参数和常见代理环境变量。

## 默认行为

- 固定使用 `Environment` 启动方案。
- 自动整理启动参数：
  - `--disable-quic`
  - `--proxy-server=<proxyUri>`
  - `--proxy-bypass-list=localhost;127.0.0.1;::1;<local>`
- 同时注入 `HTTP_PROXY`、`HTTPS_PROXY`、`ALL_PROXY`、小写同名变量和 `NO_PROXY`。
- `targetProcessNames`、运行中进程接管、UDP/WinDivert 相关配置不再作为产品入口存在。

## 配置示例

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

完整示例见 `app-proxy.example.json`。如果修改 `proxyUri`，保存或运行时会自动更新 `--proxy-server`。

## 常用命令

```powershell
EasyProxy init --config app-proxy.json
EasyProxy check --config app-proxy.json
EasyProxy run --config app-proxy.json
EasyProxy ui --config app-proxy.json
```

## 打包

```powershell
.\build-exe.bat win-x64 Release
.\build-msi.bat
.\build-setup.bat
```

发布目录只包含 EasyProxy 主程序和示例配置，不再包含 WinDivert 驱动文件。
