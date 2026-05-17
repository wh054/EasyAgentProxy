# EasyProxy

EasyProxy 是一个 .NET 控制台项目，用来按应用启动代理配置，并生成可排查代理问题的日志。

## 当前可运行能力

- `Transparent` 模式：默认模式。使用 WinDivert 驱动按目标 PID 或进程名分类连接，把目标进程的 IPv4 TCP 流量重定向到本机透明转发器，再通过 SOCKS5 或 HTTP CONNECT 上游代理转发；启用 `transparent.captureUdp` 后，IPv4 UDP 会通过 SOCKS5 UDP ASSOCIATE 转发。
- `Environment` 模式：兼容选项。启动指定应用，并注入 `HTTP_PROXY`、`HTTPS_PROXY`、`ALL_PROXY`、`NO_PROXY` 等环境变量。
- `ui` 界面：小型 WinForms 配置界面，可浏览选择目标 `.exe`、从运行中进程列表勾选目标、配置 WinDivert DLL、工作目录和日志目录，并可直接检查代理或启动目标应用。
- `check` 命令：检查代理地址解析、TCP 连通性、SOCKS5 握手，或可选的 HTTP CONNECT / SOCKS CONNECT。
- 日志：默认写入配置文件所在目录下的 `logs/app-proxy-*.log`。

`Environment` 模式不是内核级透明代理。它只能影响会读取代理环境变量的应用或运行库。

`Transparent` 模式需要管理员权限，以及 WinDivert 的 `WinDivert.dll` 和 `WinDivert64.sys` 或 `WinDivert32.sys`。当前实现覆盖目标进程的 IPv4 TCP，可选覆盖 IPv4 UDP；IPv6、WFP callout 驱动和子进程继承策略还没有实现。

## 使用

生成配置：

```powershell
dotnet run --project .\AppProxyHelper -- init --config .\app-proxy.json
```

打开界面：

```powershell
dotnet run --project .\AppProxyHelper -- ui --config .\app-proxy.json
```

也可以直接运行程序不带参数打开界面。界面中的设置会读写同一份 JSON 配置文件。

打开 UI 时，如果当前进程不是管理员权限，程序会立即弹出 UAC 并用管理员权限重新启动界面。这样使用 `Transparent` 模式时不需要等到点击“启动”才请求权限。

编辑 `app-proxy.json`：

```json
{
  "mode": "Transparent",
  "targetPath": "C:\\Path\\To\\App.exe",
  "targetProcessNames": [],
  "targetArguments": [],
  "proxyUri": "socks5://127.0.0.1:7890",
  "runDiagnosticsBeforeLaunch": true,
  "transparent": {
    "provider": "WinDivert",
    "driverPath": "WinDivert.dll",
    "redirectListenAddress": "0.0.0.0",
    "redirectListenPort": 19080,
    "captureUdp": true,
    "udpIdleTimeoutMs": 60000,
    "trackChildProcesses": true
  }
}
```

检查代理：

```powershell
dotnet run --project .\AppProxyHelper -- check --config .\app-proxy.json
```

用管理员权限启动目标应用：

```powershell
dotnet run --project .\AppProxyHelper -- run --config .\app-proxy.json
```

## WinDivert 文件

运行 `build-exe.bat` 打包时会自动下载 WinDivert 发布包，并把当前架构需要的 `WinDivert.dll` 和 `WinDivert64.sys` / `WinDivert32.sys` 放到发布目录根部。

默认 `transparent.driverPath` 是 `WinDivert.dll`，无需手动放到 `drivers` 目录。若打开驱动失败，日志会输出常见 Win32 错误的中文解释，例如权限不足、驱动签名被拒绝或系统中已有不兼容版本。

启动透明拦截时，程序会先尝试连接系统已有 WinDivert 驱动；只有系统中没有已有驱动服务时，才使用应用目录自带的 `WinDivert64.sys` 按需安装。界面中的 `环境修复` 会检查管理员权限、WinDivert 文件、Base Filtering Engine 服务和 WinDivert 服务状态；它不会自动覆盖或卸载已有 WinDivert 服务，避免影响其他软件。命令行也可以使用 `EasyProxy repair-env --config app-proxy.json` 执行同一套检查。

## 配置要点

- `proxyUri` 支持 `http://`、`https://`、`socks://`、`socks5://`。SOCKS5 支持无认证和用户名密码；HTTP/HTTPS CONNECT 支持 Basic 认证。
- `targetProcessNames` 可填写 `Codex.exe`、`Cursor.exe` 这类进程名；在 `Transparent` 模式下可以把 `targetPath` 留空，用管理员权限运行后拦截已经存在或之后出现的同名进程。
- `transparent.trackChildProcesses=true` 会同时拦截目标应用拉起的子进程，适合 Codex、Cursor、Antigravity 这类 Electron/Node 应用。
- `transparent.flowAssociationTimeoutMs` 保留为兼容字段。当前 network 包处理不再阻塞等待 PID 关联，避免影响其它应用的新连接。
- `transparent.proxyLookupTimeoutMs` 是本机透明转发器等待原始目标映射的时间。
- `transparent.captureUdp=true` 要求 `proxyUri` 使用 `socks://` 或 `socks5://`，因为 HTTP CONNECT 不提供标准 UDP 承载。
- `transparent.udpIdleTimeoutMs` 控制单个 UDP relay 在无流量后多久释放。
- `diagnostics.testProxyConnect=false` 时只检查代理本身，不主动通过代理连接外部站点。
- `abortLaunchWhenDiagnosticsFail=true` 时代理检查失败会阻止目标应用启动。
- `captureChildOutput=true` 时目标进程标准输出和错误会写入日志。

## 实现说明

WinDivert 的 network 层可以改写包，但没有 PID；flow/socket 层有 PID，但不能改包。因此透明模式使用两条路径：

1. flow/socket 层接收连接事件，按目标 PID、配置的进程名或目标子进程记录本地端口。
2. network 层拦截 outbound IPv4 TCP/UDP 包，匹配目标本地端口后把目的地址改写为本机透明转发器。
3. TCP 转发器根据原始目标地址创建 SOCKS5 或 HTTP CONNECT 上游连接。
4. UDP relay 为每个原始 UDP 目标创建本地临时端口，并通过 SOCKS5 UDP ASSOCIATE 和上游代理交换数据。
5. 本机转发器回包再由 network 层改回原始远端地址，让目标进程仍然认为自己连接的是原始目标。
