using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace AppProxyHelper;

public static class AppProxyGui
{
    public static void Run(string? configPath, bool autoRun)
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var singleInstance = GuiSingleInstance.Acquire(autoRun);
        if (!singleInstance.IsOwner)
        {
            return;
        }

        var form = new AppProxyGuiForm(configPath, autoRun);
        singleInstance.Attach(
            form,
            () => form.CanCloseForElevatedRestart,
            form.CloseForElevatedRestart,
            form.RestoreFromTray);
        Application.Run(form);
    }
}

internal sealed class AppProxyGuiForm : Form
{
    private const int SwRestore = 9;
    private const int MainWindowWidth = 1040;
    private const int MainWindowHeight = 740;
    private const int MainActionBarHeight = 42;
    private const int CompactRowHeight = 36;
    private const int ButtonHeight = 32;
    private const int HomeProxyHeight = 80;
    private const int TargetSourceHeight = 120;
    private const int ConfigGroupHeight = 82;
    private const string ManualTargetPresetText = "手动选择";

    private readonly TextBox _configPathText = new();
    private readonly ComboBox _targetPresetCombo = new();
    private readonly TextBox _targetPathText = new();
    private readonly TextBox _targetArgumentsText = new();
    private readonly TextBox _workingDirectoryText = new();
    private readonly TextBox _proxyUriText = new();
    private readonly TextBox _noProxyText = new();
    private readonly TextBox _logDirectoryText = new();
    private readonly ComboBox _minimumLogLevelCombo = new();
    private readonly CheckBox _captureChildOutputCheck = new();
    private readonly CheckBox _injectProxyEnvironmentCheck = new();
    private readonly CheckBox _waitForExitCheck = new();
    private readonly CheckBox _runDiagnosticsCheck = new();
    private readonly CheckBox _abortOnDiagnosticsFailCheck = new();

    private readonly NumericUpDown _tcpConnectTimeoutNumber = new();
    private readonly CheckBox _testProxyHandshakeCheck = new();
    private readonly CheckBox _testProxyConnectCheck = new();
    private readonly CheckBox _testProxyTlsHandshakeCheck = new();
    private readonly TextBox _connectTestHostText = new();
    private readonly NumericUpDown _connectTestPortNumber = new();

    private readonly TextBox _logText = new();
    private readonly Label _statusLabel = new();
    private readonly Button _checkButton = new();
    private readonly Button _runButton = new();
    private readonly Button _stopButton = new();
    private readonly MenuStrip _mainMenu = new();
    private readonly ContextMenuStrip _trayMenu = new();
    private readonly NotifyIcon _trayIcon = new();
    private readonly List<TargetApplicationPreset> _targetPresets = new();
    private CancellationTokenSource? _operationCts;
    private bool _updatingTargetPreset;
    private bool _allowClose;

    internal bool CanCloseForElevatedRestart => _operationCts is null;

    public AppProxyGuiForm(string? initialConfigPath, bool autoRun)
    {
        Text = "EasyProxy";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimumSize = new Size(MainWindowWidth, MainWindowHeight);
        MaximumSize = new Size(MainWindowWidth, MainWindowHeight);
        Size = new Size(MainWindowWidth, MainWindowHeight);
        MinimizeBox = true;
        ShowInTaskbar = true;
        Icon = LoadApplicationIcon();

        ConfigureTrayIcon();
        BuildUi();
        ConfigureTargetPresets();
        Populate(new AppProxyConfig());

        var configPath = ResolveInitialConfigPath(initialConfigPath);
        _configPathText.Text = configPath;

        if (File.Exists(configPath))
        {
            LoadConfig(configPath);
        }
        else
        {
            SetStatus("就绪。");
        }

        if (autoRun)
        {
            Shown += AutoRunOnShown;
        }
    }

    private static string ResolveInitialConfigPath(string? initialConfigPath)
    {
        if (!string.IsNullOrWhiteSpace(initialConfigPath))
        {
            return Path.GetFullPath(initialConfigPath);
        }

        var currentDirectoryConfig = Path.GetFullPath("app-proxy.json");
        if (File.Exists(currentDirectoryConfig))
        {
            return currentDirectoryConfig;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var configDirectory = string.IsNullOrWhiteSpace(localAppData)
            ? AppContext.BaseDirectory
            : Path.Combine(localAppData, "EasyProxy");

        return Path.Combine(configDirectory, "app-proxy.json");
    }

    internal void CloseForElevatedRestart()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            MinimizeToTray();
            return;
        }

        _trayIcon.Visible = false;
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayMenu.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ConfigureTrayIcon()
    {
        var openItem = new ToolStripMenuItem("打开");
        openItem.Click += (_, _) => RestoreFromTray();

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitFromTray();

        _trayMenu.Items.Add(openItem);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(exitItem);

        _trayIcon.Text = "EasyProxy";
        _trayIcon.Icon = Icon ?? SystemIcons.Application;
        _trayIcon.ContextMenuStrip = _trayMenu;
        _trayIcon.Visible = true;
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                RestoreFromTray();
            }
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void MinimizeToTray()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(MinimizeToTray));
            return;
        }

        WindowState = FormWindowState.Minimized;
        ShowInTaskbar = false;
        Hide();
    }

    internal void RestoreFromTray()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(RestoreFromTray));
            return;
        }

        ShowInTaskbar = true;
        if (!Visible)
        {
            Show();
        }

        WindowState = FormWindowState.Normal;
        NativeMethods.ShowWindow(Handle, SwRestore);
        BringToFront();
        if (!TopMost)
        {
            TopMost = true;
            TopMost = false;
        }

        Activate();
        NativeMethods.SetForegroundWindow(Handle);
    }

    private void ExitFromTray()
    {
        _allowClose = true;
        _operationCts?.Cancel();
        Close();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(0)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        root.Controls.Add(BuildMainMenu(), 0, 0);

        var content = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            Padding = new Padding(8)
        };

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildHomeTab());
        tabs.TabPages.Add(BuildAdvancedTab());
        tabs.TabPages.Add(BuildDiagnosticsTab());
        content.Controls.Add(tabs);
        root.Controls.Add(content, 0, 1);
    }

    private MenuStrip BuildMainMenu()
    {
        _mainMenu.Dock = DockStyle.Top;
        _mainMenu.Margin = new Padding(0);

        var helpMenu = new ToolStripMenuItem("帮助");
        var aboutItem = new ToolStripMenuItem("关于 EasyProxy");
        aboutItem.Click += (_, _) => ShowAboutDialog();

        helpMenu.DropDownItems.Add(aboutItem);
        _mainMenu.Items.Add(helpMenu);
        MainMenuStrip = _mainMenu;

        return _mainMenu;
    }

    private void ShowAboutDialog()
    {
        MessageBox.Show(
            this,
            $"EasyProxy{Environment.NewLine}版本: {GetApplicationVersion()}",
            "关于 EasyProxy",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private static string GetApplicationVersion()
    {
        var informationalVersion = typeof(AppProxyGui).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var metadataIndex = informationalVersion.IndexOf('+', StringComparison.Ordinal);
            return metadataIndex > 0
                ? informationalVersion[..metadataIndex]
                : informationalVersion;
        }

        return typeof(AppProxyGui).Assembly.GetName().Version?.ToString() ?? "未知";
    }

    private Control BuildConfigBar()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));

        panel.Controls.Add(MakeLabel("配置"), 0, 0);
        _configPathText.Dock = DockStyle.Fill;
        panel.Controls.Add(_configPathText, 1, 0);

        var loadButton = MakeButton("打开");
        loadButton.Click += (_, _) => BrowseConfig();
        panel.Controls.Add(loadButton, 2, 0);

        var saveButton = MakeButton("保存");
        saveButton.Click += (_, _) => SaveConfigFromUi(showSuccess: true);
        panel.Controls.Add(saveButton, 3, 0);

        var saveAsButton = MakeButton("另存为");
        saveAsButton.Click += (_, _) => SaveConfigAs();
        panel.Controls.Add(saveAsButton, 4, 0);
        return panel;
    }

    private TabPage BuildHomeTab()
    {
        var page = new TabPage("首页");
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(6)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, TargetSourceHeight));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, HomeProxyHeight));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, MainActionBarHeight));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.Controls.Add(root);

        root.Controls.Add(BuildTargetSourceGroup(), 0, 0);
        root.Controls.Add(BuildHomeProxyGroup(), 0, 1);
        root.Controls.Add(BuildActionBar(), 0, 2);
        root.Controls.Add(BuildLogGroup(), 0, 3);

        return page;
    }

    private Control BuildHomeProxyGroup()
    {
        var group = new GroupBox
        {
            Text = "代理地址",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 6)
        };

        var table = MakeSingleColumnTable(1);
        group.Controls.Add(table);
        AddRow(table, 0, "代理地址", _proxyUriText);
        return group;
    }

    private Control BuildLogGroup()
    {
        var group = new GroupBox
        {
            Text = "动态日志",
            Dock = DockStyle.Fill,
            Margin = new Padding(0)
        };

        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(6, 4, 6, 6)
        };

        _logText.Dock = DockStyle.Fill;
        _logText.Multiline = true;
        _logText.ReadOnly = true;
        _logText.ScrollBars = ScrollBars.Vertical;
        _logText.Font = new Font(FontFamily.GenericMonospace, 9);
        _logText.Margin = new Padding(0);

        panel.Controls.Add(_logText);
        group.Controls.Add(panel);
        return group;
    }

    private TabPage BuildAdvancedTab()
    {
        var page = new TabPage("高级设置");
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(6)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, ConfigGroupHeight));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.Controls.Add(root);

        root.Controls.Add(BuildConfigGroup(), 0, 0);
        root.Controls.Add(BuildAdvancedOptionsGroup(), 0, 1);
        return page;
    }

    private Control BuildConfigGroup()
    {
        var group = new GroupBox
        {
            Text = "配置文件",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 6)
        };

        group.Controls.Add(BuildConfigBar());
        return group;
    }

    private Control BuildTargetSourceGroup()
    {
        var group = new GroupBox
        {
            Text = "Electron 应用",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 6)
        };

        var table = MakeTargetSourceTable();
        group.Controls.Add(table);

        ConfigureTargetPresetCombo();
        AddRow(table, 0, "常用应用", _targetPresetCombo);

        AddRow(table, 1, "目标 exe", _targetPathText, MakeBrowseButton(() => BrowseFile(_targetPathText, "应用程序 (*.exe)|*.exe|所有文件 (*.*)|*.*")));

        return group;
    }

    private Control BuildAdvancedOptionsGroup()
    {
        var group = new GroupBox
        {
            Text = "运行与日志",
            Dock = DockStyle.Fill,
            Margin = new Padding(0)
        };

        var table = MakeSingleColumnTable(6);
        group.Controls.Add(table);

        AddRow(table, 0, "启动参数", _targetArgumentsText);
        AddRow(table, 1, "工作目录", _workingDirectoryText, MakeBrowseButton(() => BrowseFolder(_workingDirectoryText)));
        AddRow(table, 2, "NO_PROXY", _noProxyText);
        AddRow(table, 3, "日志目录", _logDirectoryText, MakeBrowseButton(() => BrowseFolder(_logDirectoryText)));

        ConfigureCombo(_minimumLogLevelCombo, "Debug", "Information", "Warning", "Error");
        AddRow(table, 4, "日志级别", _minimumLogLevelCombo);

        var checks = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            WrapContents = false,
            Margin = new Padding(3, 1, 3, 1)
        };
        ConfigureCheck(_captureChildOutputCheck, "捕获输出");
        ConfigureCheck(_waitForExitCheck, "等待退出");
        ConfigureCheck(_runDiagnosticsCheck, "启动前诊断");
        ConfigureCheck(_abortOnDiagnosticsFailCheck, "诊断失败中止");
        checks.Controls.AddRange(new Control[]
        {
            _captureChildOutputCheck,
            _waitForExitCheck,
            _runDiagnosticsCheck,
            _abortOnDiagnosticsFailCheck
        });
        AddWideRow(table, 5, "运行选项", checks);

        return group;
    }

    private TabPage BuildDiagnosticsTab()
    {
        var page = new TabPage("诊断与修复");
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 1,
            Padding = new Padding(6)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.Controls.Add(root);

        var group = new GroupBox
        {
            Text = "代理诊断",
            Dock = DockStyle.Fill,
            Margin = new Padding(0)
        };
        var table = MakeSingleColumnTable(6);
        group.Controls.Add(table);
        root.Controls.Add(group, 0, 0);

        ConfigureNumber(_tcpConnectTimeoutNumber, 100, 120000, 3000);
        AddRow(table, 0, "TCP 超时 ms", _tcpConnectTimeoutNumber);

        ConfigureCheck(_testProxyHandshakeCheck, "测试代理握手");
        AddRow(table, 1, "握手", _testProxyHandshakeCheck);

        ConfigureCheck(_testProxyConnectCheck, "测试经代理连接");
        AddRow(table, 2, "CONNECT", _testProxyConnectCheck);

        ConfigureCheck(_testProxyTlsHandshakeCheck, "测试 TLS 握手");
        AddRow(table, 3, "TLS", _testProxyTlsHandshakeCheck);

        AddRow(table, 4, "测试主机", _connectTestHostText);

        ConfigureNumber(_connectTestPortNumber, 1, 65535, 443);
        AddRow(table, 5, "测试端口", _connectTestPortNumber);
        return page;
    }

    private Control BuildActionBar()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        ConfigureButton(_checkButton, "检查");
        _checkButton.Click += async (_, _) => await RunOperationAsync("检查", RunCheckAsync);
        panel.Controls.Add(_checkButton, 0, 0);

        ConfigureButton(_runButton, "启动");
        _runButton.Click += async (_, _) => await RunOperationAsync("启动", RunTargetAsync);
        panel.Controls.Add(_runButton, 1, 0);

        ConfigureButton(_stopButton, "停止");
        _stopButton.Enabled = false;
        _stopButton.Click += (_, _) => _operationCts?.Cancel();
        panel.Controls.Add(_stopButton, 2, 0);

        var openLogButton = MakeButton("打开日志目录");
        openLogButton.Click += (_, _) => OpenLogDirectory();
        panel.Controls.Add(openLogButton, 3, 0);

        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.Margin = new Padding(8, 0, 0, 0);
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.ForeColor = SystemColors.GrayText;
        panel.Controls.Add(_statusLabel, 4, 0);

        return panel;
    }

    private void BrowseConfig()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "JSON 配置 (*.json)|*.json|所有文件 (*.*)|*.*",
            FileName = Path.GetFileName(_configPathText.Text)
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _configPathText.Text = dialog.FileName;
        LoadConfig(dialog.FileName);
    }

    private void SaveConfigAs()
    {
        if (!ChooseSavePath())
        {
            return;
        }

        SaveConfigFromUi(showSuccess: true);
    }

    private bool SaveConfigFromUi(bool showSuccess)
    {
        if (string.IsNullOrWhiteSpace(_configPathText.Text) && !ChooseSavePath())
        {
            return false;
        }

        try
        {
            ConfigLoader.Save(_configPathText.Text, Collect());
            if (showSuccess)
            {
                SetStatus("配置已保存。");
            }

            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private bool ChooseSavePath()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "JSON 配置 (*.json)|*.json|所有文件 (*.*)|*.*",
            FileName = string.IsNullOrWhiteSpace(_configPathText.Text)
                ? "app-proxy.json"
                : Path.GetFileName(_configPathText.Text)
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return false;
        }

        _configPathText.Text = dialog.FileName;
        return true;
    }

    private void LoadConfig(string path)
    {
        try
        {
            var loaded = ConfigLoader.Load(path);
            Populate(loaded.Value);
            SetStatus($"已加载配置: {path}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "加载失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private AppProxyConfig Collect()
    {
        return new AppProxyConfig
        {
            Mode = "Environment",
            TargetPath = _targetPathText.Text.Trim(),
            TargetProcessNames = Array.Empty<string>(),
            TargetArguments = SplitArguments(_targetArgumentsText.Text),
            WorkingDirectory = string.IsNullOrWhiteSpace(_workingDirectoryText.Text) ? null : _workingDirectoryText.Text.Trim(),
            ProxyUri = _proxyUriText.Text.Trim(),
            LogDirectory = _logDirectoryText.Text.Trim(),
            MinimumLogLevel = SelectedText(_minimumLogLevelCombo, "Information"),
            CaptureChildOutput = _captureChildOutputCheck.Checked,
            InjectProxyEnvironment = true,
            WaitForExit = _waitForExitCheck.Checked,
            RunDiagnosticsBeforeLaunch = _runDiagnosticsCheck.Checked,
            AbortLaunchWhenDiagnosticsFail = _abortOnDiagnosticsFailCheck.Checked,
            NoProxy = SplitCsv(_noProxyText.Text),
            Diagnostics = new DiagnosticsConfig
            {
                TcpConnectTimeoutMs = NumberValue(_tcpConnectTimeoutNumber),
                TestProxyHandshake = _testProxyHandshakeCheck.Checked,
                TestProxyConnect = _testProxyConnectCheck.Checked,
                TestProxyTlsHandshake = _testProxyTlsHandshakeCheck.Checked,
                ConnectTestHost = _connectTestHostText.Text.Trim(),
                ConnectTestPort = NumberValue(_connectTestPortNumber)
            }
        };
    }

    private void Populate(AppProxyConfig config)
    {
        _targetPathText.Text = config.TargetPath;
        _targetArgumentsText.Text = FormatArguments(config.TargetArguments);
        _workingDirectoryText.Text = config.WorkingDirectory ?? "";
        RefreshTargetPresetSelection(config.TargetPath);
        _proxyUriText.Text = config.ProxyUri;
        _logDirectoryText.Text = config.LogDirectory;
        SelectCombo(_minimumLogLevelCombo, config.MinimumLogLevel);
        _captureChildOutputCheck.Checked = config.CaptureChildOutput;
        _injectProxyEnvironmentCheck.Checked = true;
        _waitForExitCheck.Checked = config.WaitForExit;
        _runDiagnosticsCheck.Checked = config.RunDiagnosticsBeforeLaunch;
        _abortOnDiagnosticsFailCheck.Checked = config.AbortLaunchWhenDiagnosticsFail;
        _noProxyText.Text = string.Join(", ", config.NoProxy);

        _tcpConnectTimeoutNumber.Value = Clamp(config.Diagnostics.TcpConnectTimeoutMs, _tcpConnectTimeoutNumber);
        _testProxyHandshakeCheck.Checked = config.Diagnostics.TestProxyHandshake;
        _testProxyConnectCheck.Checked = config.Diagnostics.TestProxyConnect;
        _testProxyTlsHandshakeCheck.Checked = config.Diagnostics.TestProxyTlsHandshake;
        _connectTestHostText.Text = config.Diagnostics.ConnectTestHost;
        _connectTestPortNumber.Value = Clamp(config.Diagnostics.ConnectTestPort, _connectTestPortNumber);
    }

    private async void AutoRunOnShown(object? sender, EventArgs eventArgs)
    {
        Shown -= AutoRunOnShown;
        await RunOperationAsync("启动", RunTargetAsync);
    }

    private async Task RunOperationAsync(
        string name,
        Func<LoadedConfig, AppLogger, CancellationToken, Task<int>> operation)
    {
        if (_operationCts is not null)
        {
            ActivateCurrentWindow();
            return;
        }

        if (!SaveConfigFromUi(showSuccess: false))
        {
            return;
        }

        LoadedConfig loaded;
        try
        {
            loaded = ConfigLoader.Load(_configPathText.Text);
        }
        catch (Exception ex)
        {
            AppendLog($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [Error] {ex}");
            MessageBox.Show(this, ex.Message, $"{name}失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus($"{name}失败。");
            return;
        }

        _operationCts = new CancellationTokenSource();
        SetOperationState(running: true, name);

        try
        {
            using var logger = AppLogger.Create(loaded);
            logger.MessageWritten += (_, line) => AppendLog(line);

            logger.Info($"配置文件: {loaded.ConfigPath}");
            logger.Info($"日志文件: {logger.LogFilePath}");
            var exitCode = await operation(loaded, logger, _operationCts.Token);
            AppendLog($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [Information] {name}完成，退出码: {exitCode}");
            SetStatus($"{name}完成，退出码 {exitCode}。");
        }
        catch (OperationCanceledException)
        {
            AppendLog($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [Warning] {name}已取消。");
            SetStatus($"{name}已取消。");
        }
        catch (Exception ex)
        {
            AppendLog($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [Error] {ex}");
            MessageBox.Show(this, ex.Message, $"{name}失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus($"{name}失败。");
        }
        finally
        {
            _operationCts.Dispose();
            _operationCts = null;
            SetOperationState(running: false, name);
        }
    }

    private static async Task<int> RunCheckAsync(
        LoadedConfig loadedConfig,
        AppLogger logger,
        CancellationToken cancellationToken)
    {
        var ok = await ProxyDiagnostics.CheckAsync(loadedConfig.Value, logger, cancellationToken);
        return ok ? 0 : 3;
    }

    private static async Task<int> RunTargetAsync(
        LoadedConfig loadedConfig,
        AppLogger logger,
        CancellationToken cancellationToken)
    {
        if (loadedConfig.Value.RunDiagnosticsBeforeLaunch)
        {
            var diagnosticsOk = await ProxyDiagnostics.CheckAsync(loadedConfig.Value, logger, cancellationToken);
            if (!diagnosticsOk && loadedConfig.Value.AbortLaunchWhenDiagnosticsFail)
            {
                logger.Error("代理诊断失败，已按配置取消启动目标应用。");
                return 3;
            }

            if (!diagnosticsOk)
            {
                logger.Warn("代理诊断失败，但配置允许继续启动目标应用。");
            }
        }

        await using var interception = TrafficInterceptionFactory.Create(loadedConfig, logger);
        await interception.StartAsync(cancellationToken);

        var launcher = new ProcessProxyLauncher(loadedConfig, logger, interception);
        return await launcher.RunAsync(cancellationToken);
    }

    private void SetOperationState(bool running, string name)
    {
        _checkButton.Enabled = !running;
        _runButton.Enabled = !running;
        _stopButton.Enabled = running;
        if (running)
        {
            SetStatus($"{name}中...");
        }
    }

    private void ActivateCurrentWindow()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(ActivateCurrentWindow));
            return;
        }

        RestoreFromTray();
    }

    private void AppendLog(string text)
    {
        if (IsDisposed || Disposing || _logText.IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action<string>(AppendLog), text);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }

            return;
        }

        _logText.AppendText(text);
        _logText.AppendText(Environment.NewLine);
        if (_logText.TextLength <= 50000)
        {
            return;
        }

        _logText.Select(0, _logText.TextLength - 40000);
        _logText.SelectedText = "";
    }

    private void SetStatus(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string>(SetStatus), message);
            return;
        }

        _statusLabel.Text = message;
    }

    private void OpenLogDirectory()
    {
        var directory = _logDirectoryText.Text.Trim();
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = "logs";
        }

        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(_configPathText.Text));
        var fullPath = PathResolver.Resolve(directory, baseDirectory ?? Directory.GetCurrentDirectory());
        Directory.CreateDirectory(fullPath);
        Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
    }

    private void BrowseFile(TextBox target, string filter)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = filter,
            FileName = SafeGetFileName(target.Text),
            InitialDirectory = GetInitialBrowseDirectory(target.Text),
            CheckFileExists = true,
            CheckPathExists = true,
            RestoreDirectory = true,
            ValidateNames = true,
            DereferenceLinks = true,
            AutoUpgradeEnabled = false
        };

        DialogResult result;
        try
        {
            result = dialog.ShowDialog(this);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "打开文件选择失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus("打开文件选择失败。");
            return;
        }

        if (result != DialogResult.OK)
        {
            return;
        }

        target.Text = dialog.FileName;
        if (ReferenceEquals(target, _targetPathText))
        {
            RefreshTargetPresetSelection(dialog.FileName);
            if (string.IsNullOrWhiteSpace(_workingDirectoryText.Text))
            {
                _workingDirectoryText.Text = Path.GetDirectoryName(dialog.FileName) ?? "";
            }
        }
    }

    private void BrowseFolder(TextBox target)
    {
        using var dialog = new FolderBrowserDialog
        {
            SelectedPath = GetInitialBrowseDirectory(target.Text),
            UseDescriptionForTitle = true,
            Description = "选择目录"
        };

        DialogResult result;
        try
        {
            result = dialog.ShowDialog(this);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "打开目录选择失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus("打开目录选择失败。");
            return;
        }

        if (result == DialogResult.OK)
        {
            target.Text = dialog.SelectedPath;
        }
    }

    private void ConfigureTargetPresetCombo()
    {
        _targetPresetCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _targetPresetCombo.Dock = DockStyle.Fill;
        _targetPresetCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_updatingTargetPreset || _targetPresetCombo.SelectedItem is not TargetApplicationPreset preset)
            {
                return;
            }

            ApplyTargetPreset(preset);
        };
    }

    private void ConfigureTargetPresets()
    {
        _targetPresets.Clear();
        _targetPresets.AddRange(FindTargetApplicationPresets());

        _updatingTargetPreset = true;
        _targetPresetCombo.BeginUpdate();
        try
        {
            _targetPresetCombo.Items.Clear();
            _targetPresetCombo.Items.Add(ManualTargetPresetText);
            foreach (var preset in _targetPresets)
            {
                _targetPresetCombo.Items.Add(preset);
            }

            _targetPresetCombo.SelectedIndex = 0;
            _targetPresetCombo.Enabled = _targetPresetCombo.Items.Count > 1;
        }
        finally
        {
            _targetPresetCombo.EndUpdate();
            _updatingTargetPreset = false;
        }
    }

    private void ApplyTargetPreset(TargetApplicationPreset preset)
    {
        _targetPathText.Text = preset.ExecutablePath;
        _workingDirectoryText.Text = Path.GetDirectoryName(preset.ExecutablePath) ?? "";
        _targetArgumentsText.Text = FormatArguments(BuildChromiumArgumentsForCurrentProxy());

        SetStatus($"已选择常用应用: {preset.Name}");
    }

    private void RefreshTargetPresetSelection(string targetPath)
    {
        if (_targetPresetCombo.Items.Count == 0)
        {
            return;
        }

        var comparableTarget = GetComparablePath(targetPath);
        _updatingTargetPreset = true;
        try
        {
            _targetPresetCombo.SelectedIndex = 0;
            if (comparableTarget is null)
            {
                return;
            }

            for (var i = 0; i < _targetPresetCombo.Items.Count; i++)
            {
                if (_targetPresetCombo.Items[i] is not TargetApplicationPreset preset)
                {
                    continue;
                }

                var comparablePreset = GetComparablePath(preset.ExecutablePath);
                if (string.Equals(comparableTarget, comparablePreset, StringComparison.OrdinalIgnoreCase))
                {
                    _targetPresetCombo.SelectedIndex = i;
                    return;
                }
            }
        }
        finally
        {
            _updatingTargetPreset = false;
        }
    }

    private static IReadOnlyList<TargetApplicationPreset> FindTargetApplicationPresets()
    {
        var presets = new List<TargetApplicationPreset>();
        AddTargetPreset(presets, "Codex 应用", GetCodexCandidates());
        AddTargetPreset(presets, "Cursor 应用", GetCursorCandidates());
        AddTargetPreset(presets, "Antigravity 应用", GetAntigravityCandidates());
        return presets;
    }

    private string[] BuildChromiumArgumentsForCurrentProxy()
    {
        var proxyUri = string.IsNullOrWhiteSpace(_proxyUriText.Text)
            ? new AppProxyConfig().ProxyUri
            : _proxyUriText.Text.Trim();
        return ConfigLoader.BuildDefaultChromiumProxyArguments(proxyUri);
    }

    private static void AddTargetPreset(
        ICollection<TargetApplicationPreset> presets,
        string name,
        IEnumerable<string> candidates,
        params string[] arguments)
    {
        var executablePath = FindFirstExistingFile(candidates);
        if (executablePath is not null)
        {
            presets.Add(new TargetApplicationPreset(name, executablePath, arguments));
        }
    }

    private static string? FindFirstExistingFile(IEnumerable<string> candidates)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(candidate);
            }
            catch
            {
                continue;
            }

            if (!seen.Add(fullPath))
            {
                continue;
            }

            try
            {
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
            catch
            {
                // Some packaged app directories can be permission-restricted.
            }
        }

        return null;
    }

    private static Icon LoadApplicationIcon()
    {
        return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
    }

    private static IEnumerable<string> GetCodexCandidates()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.Combine(
                programFiles,
                @"WindowsApps\OpenAI.Codex_26.506.3741.0_x64__2p2nqsd0c76g0\app\Codex.exe");

            var windowsApps = Path.Combine(programFiles, "WindowsApps");
            foreach (var packageDirectory in EnumerateDirectories(windowsApps, "OpenAI.Codex_*_x64__2p2nqsd0c76g0"))
            {
                yield return Path.Combine(packageDirectory, "app", "Codex.exe");
            }
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "Programs", "Codex", "Codex.exe");
            yield return Path.Combine(localAppData, "Programs", "codex", "Codex.exe");
        }
    }

    private static IEnumerable<string> GetCursorCandidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "Programs", "cursor", "Cursor.exe");
            yield return Path.Combine(localAppData, "Programs", "Cursor", "Cursor.exe");
        }

        foreach (var programFiles in GetProgramFilesDirectories())
        {
            yield return Path.Combine(programFiles, "Cursor", "Cursor.exe");
        }
    }

    private static IEnumerable<string> GetAntigravityCandidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "Programs", "Antigravity", "Antigravity.exe");
            yield return Path.Combine(localAppData, "Programs", "antigravity", "Antigravity.exe");
        }

        foreach (var programFiles in GetProgramFilesDirectories())
        {
            yield return Path.Combine(programFiles, "Antigravity", "Antigravity.exe");
        }
    }

    private static IEnumerable<string> GetProgramFilesDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        })
        {
            if (!string.IsNullOrWhiteSpace(folder) && seen.Add(folder))
            {
                yield return folder;
            }
        }
    }

    private static IEnumerable<string> EnumerateDirectories(string root, string pattern)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            yield break;
        }

        List<string> directories;
        try
        {
            directories = Directory
                .EnumerateDirectories(root, pattern, SearchOption.TopDirectoryOnly)
                .OrderByDescending(GetSafeLastWriteTimeUtc)
                .ToList();
        }
        catch
        {
            yield break;
        }

        foreach (var directory in directories)
        {
            yield return directory;
        }
    }

    private static DateTime GetSafeLastWriteTimeUtc(string path)
    {
        try
        {
            return Directory.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static string? GetComparablePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return null;
        }
    }

    private static TableLayoutPanel MakeSingleColumnTable(int rows)
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = rows + 1,
            Padding = new Padding(6)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 32));
        for (var i = 0; i < rows; i++)
        {
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, CompactRowHeight));
        }
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        return table;
    }

    private static TableLayoutPanel MakeTargetSourceTable()
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 3,
            Padding = new Padding(6, 8, 6, 4)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        for (var i = 0; i < 2; i++)
        {
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, CompactRowHeight));
        }
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        return table;
    }

    private static void AddRow(TableLayoutPanel table, int row, string label, Control input, Control? button = null)
    {
        table.Controls.Add(MakeLabel(label), 0, row);
        ConfigureCellControl(input);
        table.Controls.Add(input, 1, row);
        if (button is not null)
        {
            table.Controls.Add(button, 2, row);
        }
    }

    private static void AddWideRow(TableLayoutPanel table, int row, string label, Control input)
    {
        table.Controls.Add(MakeLabel(label), 0, row);
        ConfigureCellControl(input);
        table.Controls.Add(input, 1, row);
        table.SetColumnSpan(input, 2);
    }

    private static void AddPairRow(
        TableLayoutPanel table,
        int row,
        string leftLabel,
        Control leftInput,
        Control? leftButton,
        string rightLabel,
        Control rightInput,
        Control? rightButton)
    {
        table.Controls.Add(MakeLabel(leftLabel), 0, row);
        ConfigureCellControl(leftInput);
        table.Controls.Add(leftInput, 1, row);
        if (leftButton is not null)
        {
            table.Controls.Add(leftButton, 2, row);
        }

        table.Controls.Add(MakeLabel(rightLabel), 3, row);
        ConfigureCellControl(rightInput);
        table.Controls.Add(rightInput, 4, row);
        if (rightButton is not null)
        {
            table.Controls.Add(rightButton, 5, row);
        }
    }

    private static void ConfigureCellControl(Control control)
    {
        control.Dock = DockStyle.Fill;
        if (control is CheckBox)
        {
            control.Margin = new Padding(3, 5, 3, 1);
            return;
        }

        control.Margin = new Padding(3, 2, 3, 2);
    }

    private static Label MakeLabel(string text)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 4, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
    }

    private static Button MakeButton(string text)
    {
        var button = new Button();
        ConfigureButton(button, text);
        return button;
    }

    private static void ConfigureButton(Button button, string text)
    {
        button.Text = text;
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(2, 2, 2, 2);
        button.Padding = new Padding(0, 1, 0, 1);
        button.MinimumSize = new Size(0, ButtonHeight);
        button.TextAlign = ContentAlignment.MiddleCenter;
        button.AutoEllipsis = true;
    }

    private static Button MakeBrowseButton(Action action)
    {
        var button = MakeButton("...");
        button.Click += (_, _) => action();
        return button;
    }

    private static void ConfigureCombo(ComboBox combo, params string[] values)
    {
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.Items.Clear();
        combo.Items.AddRange(values);
        combo.Dock = DockStyle.Fill;
        if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }
    }

    private static void ConfigureCheck(CheckBox check, string text)
    {
        check.Text = text;
        check.AutoSize = true;
        check.TextAlign = ContentAlignment.MiddleLeft;
        check.Margin = new Padding(3, 4, 12, 0);
    }

    private static void ConfigureNumber(NumericUpDown number, int min, int max, int value)
    {
        number.Minimum = min;
        number.Maximum = max;
        number.Value = Clamp(value, number);
        number.ThousandsSeparator = true;
        number.Dock = DockStyle.Fill;
        number.Margin = new Padding(3, 2, 3, 2);
    }

    private static decimal Clamp(int value, NumericUpDown number)
    {
        return Math.Min(number.Maximum, Math.Max(number.Minimum, value));
    }

    private static int NumberValue(NumericUpDown number)
    {
        return decimal.ToInt32(number.Value);
    }

    private static void SelectCombo(ComboBox combo, string value)
    {
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i]?.ToString()?.Equals(value, StringComparison.OrdinalIgnoreCase) == true)
            {
                combo.SelectedIndex = i;
                return;
            }
        }

        if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }
    }

    private static string SelectedText(ComboBox combo, string fallback)
    {
        return combo.SelectedItem?.ToString() ?? fallback;
    }

    private static string[] SplitCsv(string value)
    {
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string[] SplitArguments(string text)
    {
        var args = new List<string>();
        var builder = new StringBuilder();
        var inQuote = false;
        var tokenStarted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '"')
            {
                inQuote = !inQuote;
                tokenStarted = true;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuote)
            {
                FlushArgument(args, builder, ref tokenStarted);
                continue;
            }

            if (ch == '\\' && i + 1 < text.Length && text[i + 1] == '"')
            {
                builder.Append('"');
                tokenStarted = true;
                i++;
                continue;
            }

            builder.Append(ch);
            tokenStarted = true;
        }

        if (inQuote)
        {
            throw new InvalidOperationException("启动参数中的引号未闭合。");
        }

        FlushArgument(args, builder, ref tokenStarted);
        return args.ToArray();
    }

    private static void FlushArgument(List<string> args, StringBuilder builder, ref bool tokenStarted)
    {
        if (!tokenStarted)
        {
            return;
        }

        args.Add(builder.ToString());
        builder.Clear();
        tokenStarted = false;
    }

    private static string FormatArguments(string[] arguments)
    {
        return string.Join(" ", arguments.Select(static argument =>
            argument.Length == 0 || argument.Any(char.IsWhiteSpace) || argument.Contains('"', StringComparison.Ordinal)
                ? $"\"{argument.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
                : argument));
    }

    private static string SafeGetFileName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        try
        {
            return Path.GetFileName(path);
        }
        catch
        {
            return "";
        }
    }

    private static string GetInitialBrowseDirectory(string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && TryGetSafeLocalDirectory(path, out var directory))
        {
            return directory;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles) && Directory.Exists(programFiles))
        {
            return programFiles;
        }

        return Environment.CurrentDirectory;
    }

    private static bool TryGetSafeLocalDirectory(string path, out string directory)
    {
        directory = "";

        try
        {
            var candidate = path;
            if (!IsSafeLocalPath(candidate))
            {
                return false;
            }

            if (!Directory.Exists(candidate))
            {
                candidate = Path.GetDirectoryName(candidate) ?? "";
                if (!IsSafeLocalPath(candidate) || !Directory.Exists(candidate))
                {
                    return false;
                }
            }

            directory = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSafeLocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        var root = Path.GetPathRoot(path);
        return !string.IsNullOrEmpty(root) && !root.StartsWith(@"\\", StringComparison.Ordinal);
    }

    private sealed class TargetApplicationPreset
    {
        public TargetApplicationPreset(string name, string executablePath, string[] arguments)
        {
            Name = name;
            ExecutablePath = executablePath;
            Arguments = arguments;
        }

        public string Name { get; }

        public string ExecutablePath { get; }

        public string[] Arguments { get; }

        public override string ToString()
        {
            return Name;
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
