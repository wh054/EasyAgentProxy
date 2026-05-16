using System.Diagnostics;
using System.Drawing;
using System.Security.Principal;
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
        singleInstance.Attach(form, () => form.CanCloseForElevatedRestart, form.CloseForElevatedRestart);
        Application.Run(form);
    }
}

internal sealed class AppProxyGuiForm : Form
{
    private const int ErrorCancelled = 1223;
    private const int MainWindowWidth = 1040;
    private const int MainWindowHeight = 740;
    private const int MainConfigBarHeight = 32;
    private const int MainActionBarHeight = 38;
    private const int MainLogHeight = 112;
    private const int CompactRowHeight = 32;
    private const int TargetSourceHeight = 146;
    private const string ManualTargetPresetText = "手动选择";

    private readonly TextBox _configPathText = new();
    private readonly ComboBox _modeCombo = new();
    private readonly ComboBox _targetPresetCombo = new();
    private readonly TextBox _targetPathText = new();
    private readonly TextBox _targetProcessNamesText = new();
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
    private readonly TextBox _connectTestHostText = new();
    private readonly NumericUpDown _connectTestPortNumber = new();

    private readonly TextBox _providerText = new();
    private readonly TextBox _driverPathText = new();
    private readonly TextBox _redirectAddressText = new();
    private readonly NumericUpDown _redirectPortNumber = new();
    private readonly NumericUpDown _winDivertPriorityNumber = new();
    private readonly NumericUpDown _flowAssociationTimeoutNumber = new();
    private readonly NumericUpDown _proxyLookupTimeoutNumber = new();
    private readonly NumericUpDown _proxyConnectTimeoutNumber = new();
    private readonly NumericUpDown _listenBacklogNumber = new();
    private readonly NumericUpDown _packetBufferSizeNumber = new();
    private readonly NumericUpDown _queueLengthNumber = new();
    private readonly NumericUpDown _queueTimeNumber = new();
    private readonly NumericUpDown _queueSizeNumber = new();
    private readonly CheckBox _captureUdpCheck = new();
    private readonly NumericUpDown _udpIdleTimeoutNumber = new();
    private readonly CheckBox _trackChildProcessesCheck = new();
    private readonly TextBox _excludedChildProcessNamesText = new();

    private readonly Panel _transparentPanel = new();
    private readonly TextBox _logText = new();
    private readonly Label _statusLabel = new();
    private readonly Button _checkButton = new();
    private readonly Button _runButton = new();
    private readonly Button _stopButton = new();
    private Button? _selectProcessButton;
    private readonly ContextMenuStrip _trayMenu = new();
    private readonly NotifyIcon _trayIcon = new();
    private readonly List<TargetApplicationPreset> _targetPresets = new();
    private CancellationTokenSource? _operationCts;
    private bool _updatingTargetPreset;
    private bool _elevationInProgress;
    private bool _allowClose;

    internal bool CanCloseForElevatedRestart => _operationCts is null;

    public AppProxyGuiForm(string? initialConfigPath, bool autoRun)
    {
        Text = "AppProxyHelper";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimumSize = new Size(MainWindowWidth, MainWindowHeight);
        MaximumSize = new Size(MainWindowWidth, MainWindowHeight);
        Size = new Size(MainWindowWidth, MainWindowHeight);
        MinimizeBox = true;
        ShowInTaskbar = true;

        ConfigureTrayIcon();
        BuildUi();
        ConfigureTargetPresets();
        Populate(new AppProxyConfig());

        var configPath = !string.IsNullOrWhiteSpace(initialConfigPath)
            ? Path.GetFullPath(initialConfigPath)
            : Path.GetFullPath("app-proxy.json");
        _configPathText.Text = configPath;

        if (File.Exists(configPath))
        {
            LoadConfig(configPath);
        }

        if (autoRun)
        {
            Shown += AutoRunOnShown;
        }
    }

    internal void CloseForElevatedRestart()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose && !_elevationInProgress && e.CloseReason == CloseReason.UserClosing)
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

        _trayIcon.Text = "AppProxyHelper";
        _trayIcon.Icon = Icon ?? SystemIcons.Application;
        _trayIcon.ContextMenuStrip = _trayMenu;
        _trayIcon.Visible = true;
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

    private void RestoreFromTray()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(RestoreFromTray));
            return;
        }

        ShowInTaskbar = true;
        Show();
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        Activate();
        BringToFront();
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
            RowCount = 4,
            Padding = new Padding(8)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, MainConfigBarHeight));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, MainActionBarHeight));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, MainLogHeight));
        Controls.Add(root);

        root.Controls.Add(BuildConfigBar(), 0, 0);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildTargetTab());
        tabs.TabPages.Add(BuildDiagnosticsTab());
        tabs.TabPages.Add(BuildTransparentTab());
        root.Controls.Add(tabs, 0, 1);

        root.Controls.Add(BuildActionBar(), 0, 2);

        _logText.Dock = DockStyle.Fill;
        _logText.Multiline = true;
        _logText.ReadOnly = true;
        _logText.ScrollBars = ScrollBars.Vertical;
        _logText.Font = new Font(FontFamily.GenericMonospace, 9);
        _logText.Margin = new Padding(0);
        root.Controls.Add(_logText, 0, 3);
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

    private TabPage BuildTargetTab()
    {
        var page = new TabPage("目标/代理");
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(6)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, TargetSourceHeight));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.Controls.Add(root);

        root.Controls.Add(BuildTargetSourceGroup(), 0, 0);
        root.Controls.Add(BuildTargetOptionsGroup(), 0, 1);

        return page;
    }

    private Control BuildTargetSourceGroup()
    {
        var group = new GroupBox
        {
            Text = "目标来源",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 6)
        };

        var table = MakeTargetSourceTable();
        group.Controls.Add(table);

        ConfigureTargetPresetCombo();
        AddRow(table, 0, "常用应用", _targetPresetCombo);

        AddRow(table, 1, "手动 exe", _targetPathText, MakeBrowseButton(() => BrowseFile(_targetPathText, "应用程序 (*.exe)|*.exe|所有文件 (*.*)|*.*")));

        _selectProcessButton = MakeButton("从进程中选择");
        _selectProcessButton.Click += (_, _) => SelectTargetProcesses();
        AddRow(table, 2, "运行中进程", _targetProcessNamesText, _selectProcessButton);

        return group;
    }

    private Control BuildTargetOptionsGroup()
    {
        var group = new GroupBox
        {
            Text = "代理与运行",
            Dock = DockStyle.Fill,
            Margin = new Padding(0)
        };

        var table = MakeSingleColumnTable(8);
        group.Controls.Add(table);

        ConfigureCombo(_modeCombo, "Transparent", "Environment");
        _modeCombo.SelectedIndexChanged += (_, _) => RefreshModeState();
        AddRow(table, 0, "模式", _modeCombo);

        AddRow(table, 1, "启动参数", _targetArgumentsText);
        AddRow(table, 2, "工作目录", _workingDirectoryText, MakeBrowseButton(() => BrowseFolder(_workingDirectoryText)));
        AddRow(table, 3, "代理 URI", _proxyUriText);
        AddRow(table, 4, "NO_PROXY", _noProxyText);
        AddRow(table, 5, "日志目录", _logDirectoryText, MakeBrowseButton(() => BrowseFolder(_logDirectoryText)));

        ConfigureCombo(_minimumLogLevelCombo, "Debug", "Information", "Warning", "Error");
        AddRow(table, 6, "日志级别", _minimumLogLevelCombo);

        var checks = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            WrapContents = false,
            Margin = new Padding(3, 1, 3, 1)
        };
        ConfigureCheck(_captureChildOutputCheck, "捕获输出");
        ConfigureCheck(_injectProxyEnvironmentCheck, "注入环境变量");
        ConfigureCheck(_waitForExitCheck, "等待退出");
        ConfigureCheck(_runDiagnosticsCheck, "启动前诊断");
        ConfigureCheck(_abortOnDiagnosticsFailCheck, "诊断失败中止");
        checks.Controls.AddRange(new Control[]
        {
            _captureChildOutputCheck,
            _injectProxyEnvironmentCheck,
            _waitForExitCheck,
            _runDiagnosticsCheck,
            _abortOnDiagnosticsFailCheck
        });
        AddWideRow(table, 7, "运行选项", checks);

        return group;
    }

    private TabPage BuildDiagnosticsTab()
    {
        var page = new TabPage("诊断");
        var table = MakeSingleColumnTable(5);
        page.Controls.Add(table);

        ConfigureNumber(_tcpConnectTimeoutNumber, 100, 120000, 3000);
        AddRow(table, 0, "TCP 超时 ms", _tcpConnectTimeoutNumber);

        ConfigureCheck(_testProxyHandshakeCheck, "测试代理握手");
        AddRow(table, 1, "握手", _testProxyHandshakeCheck);

        ConfigureCheck(_testProxyConnectCheck, "测试经代理连接");
        AddRow(table, 2, "CONNECT", _testProxyConnectCheck);

        AddRow(table, 3, "测试主机", _connectTestHostText);

        ConfigureNumber(_connectTestPortNumber, 1, 65535, 443);
        AddRow(table, 4, "测试端口", _connectTestPortNumber);
        return page;
    }

    private TabPage BuildTransparentTab()
    {
        var page = new TabPage("透明拦截");
        _transparentPanel.Dock = DockStyle.Fill;
        _transparentPanel.Margin = new Padding(0);
        page.Controls.Add(_transparentPanel);

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 6,
            RowCount = 10,
            Padding = new Padding(6)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 142));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 142));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
        for (var i = 0; i < 9; i++)
        {
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, CompactRowHeight));
        }
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _transparentPanel.Controls.Add(table);

        _providerText.ReadOnly = true;
        AddPairRow(
            table,
            0,
            "Provider",
            _providerText,
            null,
            "WinDivert DLL",
            _driverPathText,
            MakeBrowseButton(() => BrowseFile(_driverPathText, "WinDivert.dll|WinDivert.dll|DLL 文件 (*.dll)|*.dll|所有文件 (*.*)|*.*")));

        ConfigureNumber(_redirectPortNumber, 1, 65535, 19080);
        AddPairRow(table, 1, "监听地址", _redirectAddressText, null, "监听端口", _redirectPortNumber, null);

        ConfigureNumber(_winDivertPriorityNumber, -1000, 1000, 0);
        ConfigureCheck(_captureUdpCheck, "捕获 UDP");
        AddPairRow(table, 2, "优先级", _winDivertPriorityNumber, null, "UDP", _captureUdpCheck, null);

        ConfigureNumber(_flowAssociationTimeoutNumber, 0, 60000, 0);
        ConfigureNumber(_proxyLookupTimeoutNumber, 0, 120000, 3000);
        AddPairRow(table, 3, "Flow 关联 ms", _flowAssociationTimeoutNumber, null, "映射等待 ms", _proxyLookupTimeoutNumber, null);

        ConfigureNumber(_proxyConnectTimeoutNumber, 100, 120000, 10000);
        ConfigureNumber(_udpIdleTimeoutNumber, 1000, 86400000, 60000);
        AddPairRow(table, 4, "代理连接 ms", _proxyConnectTimeoutNumber, null, "UDP 空闲 ms", _udpIdleTimeoutNumber, null);

        ConfigureNumber(_listenBacklogNumber, 1, 8192, 512);
        ConfigureNumber(_packetBufferSizeNumber, 1500, 1048576, 65535);
        AddPairRow(table, 5, "Listen backlog", _listenBacklogNumber, null, "包缓冲", _packetBufferSizeNumber, null);

        ConfigureNumber(_queueLengthNumber, 1, 65535, 4096);
        ConfigureNumber(_queueTimeNumber, 1, 60000, 2048);
        AddPairRow(table, 6, "队列长度", _queueLengthNumber, null, "队列时间 ms", _queueTimeNumber, null);

        ConfigureNumber(_queueSizeNumber, 1, int.MaxValue, 4 * 1024 * 1024);
        ConfigureCheck(_trackChildProcessesCheck, "跟踪子进程");
        AddPairRow(table, 7, "队列字节", _queueSizeNumber, null, "子进程", _trackChildProcessesCheck, null);

        table.Controls.Add(MakeLabel("排除子进程"), 0, 8);
        ConfigureCellControl(_excludedChildProcessNamesText);
        table.Controls.Add(_excludedChildProcessNamesText, 1, 8);
        table.SetColumnSpan(_excludedChildProcessNamesText, 5);
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

        _checkButton.Text = "检查";
        _checkButton.Dock = DockStyle.Fill;
        _checkButton.Click += async (_, _) => await RunOperationAsync("检查", RunCheckAsync);
        panel.Controls.Add(_checkButton, 0, 0);

        _runButton.Text = "启动";
        _runButton.Dock = DockStyle.Fill;
        _runButton.Click += async (_, _) => await RunOperationAsync("启动", RunTargetAsync, requestElevation: true);
        panel.Controls.Add(_runButton, 1, 0);

        _stopButton.Text = "停止";
        _stopButton.Dock = DockStyle.Fill;
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
            _elevationInProgress = false;
            SetElevationRequestState(pending: false);
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
            Mode = SelectedText(_modeCombo, "Transparent"),
            TargetPath = _targetPathText.Text.Trim(),
            TargetProcessNames = SplitCsv(_targetProcessNamesText.Text),
            TargetArguments = SplitArguments(_targetArgumentsText.Text),
            WorkingDirectory = string.IsNullOrWhiteSpace(_workingDirectoryText.Text) ? null : _workingDirectoryText.Text.Trim(),
            ProxyUri = _proxyUriText.Text.Trim(),
            LogDirectory = _logDirectoryText.Text.Trim(),
            MinimumLogLevel = SelectedText(_minimumLogLevelCombo, "Information"),
            CaptureChildOutput = _captureChildOutputCheck.Checked,
            InjectProxyEnvironment = _injectProxyEnvironmentCheck.Checked,
            WaitForExit = _waitForExitCheck.Checked,
            RunDiagnosticsBeforeLaunch = _runDiagnosticsCheck.Checked,
            AbortLaunchWhenDiagnosticsFail = _abortOnDiagnosticsFailCheck.Checked,
            NoProxy = SplitCsv(_noProxyText.Text),
            Diagnostics = new DiagnosticsConfig
            {
                TcpConnectTimeoutMs = NumberValue(_tcpConnectTimeoutNumber),
                TestProxyHandshake = _testProxyHandshakeCheck.Checked,
                TestProxyConnect = _testProxyConnectCheck.Checked,
                ConnectTestHost = _connectTestHostText.Text.Trim(),
                ConnectTestPort = NumberValue(_connectTestPortNumber)
            },
            Transparent = new TransparentInterceptionConfig
            {
                Provider = string.IsNullOrWhiteSpace(_providerText.Text) ? "WinDivert" : _providerText.Text.Trim(),
                DriverPath = _driverPathText.Text.Trim(),
                RedirectListenAddress = _redirectAddressText.Text.Trim(),
                RedirectListenPort = NumberValue(_redirectPortNumber),
                WinDivertPriority = NumberValue(_winDivertPriorityNumber),
                FlowAssociationTimeoutMs = NumberValue(_flowAssociationTimeoutNumber),
                ProxyLookupTimeoutMs = NumberValue(_proxyLookupTimeoutNumber),
                ProxyConnectTimeoutMs = NumberValue(_proxyConnectTimeoutNumber),
                ListenBacklog = NumberValue(_listenBacklogNumber),
                PacketBufferSize = NumberValue(_packetBufferSizeNumber),
                QueueLength = NumberValue(_queueLengthNumber),
                QueueTimeMs = NumberValue(_queueTimeNumber),
                QueueSizeBytes = NumberValue(_queueSizeNumber),
                CaptureUdp = _captureUdpCheck.Checked,
                UdpIdleTimeoutMs = NumberValue(_udpIdleTimeoutNumber),
                TrackChildProcesses = _trackChildProcessesCheck.Checked,
                ExcludedChildProcessNames = SplitCsv(_excludedChildProcessNamesText.Text)
            }
        };
    }

    private void Populate(AppProxyConfig config)
    {
        SelectCombo(_modeCombo, config.Mode);
        _targetPathText.Text = config.TargetPath;
        _targetProcessNamesText.Text = string.Join(", ", config.TargetProcessNames);
        _targetArgumentsText.Text = FormatArguments(config.TargetArguments);
        _workingDirectoryText.Text = config.WorkingDirectory ?? "";
        RefreshTargetPresetSelection(config.TargetPath);
        _proxyUriText.Text = config.ProxyUri;
        _logDirectoryText.Text = config.LogDirectory;
        SelectCombo(_minimumLogLevelCombo, config.MinimumLogLevel);
        _captureChildOutputCheck.Checked = config.CaptureChildOutput;
        _injectProxyEnvironmentCheck.Checked = config.InjectProxyEnvironment;
        _waitForExitCheck.Checked = config.WaitForExit;
        _runDiagnosticsCheck.Checked = config.RunDiagnosticsBeforeLaunch;
        _abortOnDiagnosticsFailCheck.Checked = config.AbortLaunchWhenDiagnosticsFail;
        _noProxyText.Text = string.Join(", ", config.NoProxy);

        _tcpConnectTimeoutNumber.Value = Clamp(config.Diagnostics.TcpConnectTimeoutMs, _tcpConnectTimeoutNumber);
        _testProxyHandshakeCheck.Checked = config.Diagnostics.TestProxyHandshake;
        _testProxyConnectCheck.Checked = config.Diagnostics.TestProxyConnect;
        _connectTestHostText.Text = config.Diagnostics.ConnectTestHost;
        _connectTestPortNumber.Value = Clamp(config.Diagnostics.ConnectTestPort, _connectTestPortNumber);

        _providerText.Text = config.Transparent.Provider;
        _driverPathText.Text = config.Transparent.DriverPath;
        _redirectAddressText.Text = config.Transparent.RedirectListenAddress;
        _redirectPortNumber.Value = Clamp(config.Transparent.RedirectListenPort, _redirectPortNumber);
        _winDivertPriorityNumber.Value = Clamp(config.Transparent.WinDivertPriority, _winDivertPriorityNumber);
        _flowAssociationTimeoutNumber.Value = Clamp(config.Transparent.FlowAssociationTimeoutMs, _flowAssociationTimeoutNumber);
        _proxyLookupTimeoutNumber.Value = Clamp(config.Transparent.ProxyLookupTimeoutMs, _proxyLookupTimeoutNumber);
        _proxyConnectTimeoutNumber.Value = Clamp(config.Transparent.ProxyConnectTimeoutMs, _proxyConnectTimeoutNumber);
        _listenBacklogNumber.Value = Clamp(config.Transparent.ListenBacklog, _listenBacklogNumber);
        _packetBufferSizeNumber.Value = Clamp(config.Transparent.PacketBufferSize, _packetBufferSizeNumber);
        _queueLengthNumber.Value = Clamp(config.Transparent.QueueLength, _queueLengthNumber);
        _queueTimeNumber.Value = Clamp(config.Transparent.QueueTimeMs, _queueTimeNumber);
        _queueSizeNumber.Value = Clamp(config.Transparent.QueueSizeBytes, _queueSizeNumber);
        _captureUdpCheck.Checked = config.Transparent.CaptureUdp;
        _udpIdleTimeoutNumber.Value = Clamp(config.Transparent.UdpIdleTimeoutMs, _udpIdleTimeoutNumber);
        _trackChildProcessesCheck.Checked = config.Transparent.TrackChildProcesses;
        _excludedChildProcessNamesText.Text = string.Join(", ", config.Transparent.ExcludedChildProcessNames);
        RefreshModeState();
    }

    private async void AutoRunOnShown(object? sender, EventArgs eventArgs)
    {
        Shown -= AutoRunOnShown;
        await RunOperationAsync("启动", RunTargetAsync, requestElevation: true);
    }

    private async Task RunOperationAsync(
        string name,
        Func<LoadedConfig, AppLogger, CancellationToken, Task<int>> operation,
        bool requestElevation = false)
    {
        if (_operationCts is not null || _elevationInProgress)
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

        if (requestElevation && ShouldRequestElevation(loaded.Value))
        {
            RequestElevatedAutoRun(loaded.ConfigPath);
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

    private void RequestElevatedAutoRun(string configPath)
    {
        if (_elevationInProgress)
        {
            ActivateCurrentWindow();
            return;
        }

        _elevationInProgress = true;
        SetElevationRequestState(pending: true);

        try
        {
            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                executablePath = Application.ExecutablePath;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory
            };
            startInfo.ArgumentList.Add("ui");
            startInfo.ArgumentList.Add("--config");
            startInfo.ArgumentList.Add(configPath);
            startInfo.ArgumentList.Add("--autorun");

            if (Process.Start(startInfo) is null)
            {
                throw new InvalidOperationException("无法启动管理员权限实例。");
            }

            BeginInvoke(new Action(Close));

            AppendLog($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [Information] 已请求管理员权限，新窗口将自动启动目标应用。");
            SetStatus("已请求管理员权限，请在新窗口查看运行状态。");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            _elevationInProgress = false;
            SetElevationRequestState(pending: false);
            AppendLog($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [Warning] 已取消管理员权限请求。");
            SetStatus("已取消管理员权限请求。");
        }
        catch (Exception ex)
        {
            AppendLog($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [Error] 请求管理员权限失败: {ex}");
            MessageBox.Show(this, ex.Message, "请求管理员权限失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus("请求管理员权限失败。");
        }
    }

    private static bool ShouldRequestElevation(AppProxyConfig config)
    {
        return OperatingSystem.IsWindows()
            && config.Mode.Equals("Transparent", StringComparison.OrdinalIgnoreCase)
            && !IsAdministrator();
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
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

    private void SetElevationRequestState(bool pending)
    {
        _checkButton.Enabled = !pending && _operationCts is null;
        _runButton.Enabled = !pending && _operationCts is null;
        _stopButton.Enabled = _operationCts is not null;
        if (pending)
        {
            SetStatus("已请求管理员权限，请完成 UAC 确认。");
        }
    }

    private void ActivateCurrentWindow()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(ActivateCurrentWindow));
            return;
        }

        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        Show();
        Activate();
        BringToFront();
    }

    private void RefreshModeState()
    {
        var transparent = SelectedText(_modeCombo, "Transparent").Equals("Transparent", StringComparison.OrdinalIgnoreCase);
        _transparentPanel.Enabled = transparent;
        _targetProcessNamesText.Enabled = transparent;
        if (_selectProcessButton is not null)
        {
            _selectProcessButton.Enabled = transparent;
        }
    }

    private void AppendLog(string text)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string>(AppendLog), text);
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

    private void SelectTargetProcesses()
    {
        using var dialog = new ProcessSelectionDialog(SplitCsv(_targetProcessNamesText.Text));
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _targetProcessNamesText.Text = string.Join(", ", dialog.SelectedProcessNames);
        SetStatus($"已选择 {dialog.SelectedProcessNames.Length} 个运行中进程。");
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
        if (preset.Arguments.Length > 0 && string.IsNullOrWhiteSpace(_targetArgumentsText.Text))
        {
            _targetArgumentsText.Text = FormatArguments(preset.Arguments);
        }

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
        AddTargetPreset(presets, "Codex 应用", GetCodexCandidates(), "--disable-quic");
        AddTargetPreset(presets, "Cursor 应用", GetCursorCandidates(), "--disable-quic");
        AddTargetPreset(presets, "Antigravity 应用", GetAntigravityCandidates(), "--disable-quic");
        return presets;
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
            RowCount = 4,
            Padding = new Padding(6, 8, 6, 4)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        for (var i = 0; i < 3; i++)
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
        return new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            Margin = new Padding(2, 1, 2, 1),
            MinimumSize = new Size(0, 28)
        };
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

    private sealed class ProcessSelectionDialog : Form
    {
        private readonly TextBox _filterText = new();
        private readonly ListView _processList = new();
        private readonly Button _okButton = new();
        private readonly Button _refreshButton = new();
        private readonly ListViewGroup _applicationGroup = new("应用", HorizontalAlignment.Left);
        private readonly ListViewGroup _backgroundGroup = new("后台进程", HorizontalAlignment.Left);
        private readonly HashSet<string> _checkedProcessNames;
        private readonly List<ProcessListItem> _processes = new();
        private bool _updatingList;

        public ProcessSelectionDialog(IEnumerable<string> selectedProcessNames)
        {
            _checkedProcessNames = selectedProcessNames
                .Select(NormalizeProcessName)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            Text = "从进程中选择";
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = true;
            ShowInTaskbar = false;
            Size = new Size(760, 520);
            MinimumSize = new Size(620, 380);

            BuildUi();
            LoadProcesses();
        }

        public string[] SelectedProcessNames { get; private set; } = Array.Empty<string>();

        private void BuildUi()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(8)
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            Controls.Add(root);

            var filterRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3
            };
            filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
            filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
            filterRow.Controls.Add(MakeLabel("筛选"), 0, 0);
            _filterText.Dock = DockStyle.Fill;
            _filterText.TextChanged += (_, _) => ApplyFilter();
            filterRow.Controls.Add(_filterText, 1, 0);
            _refreshButton.Text = "刷新";
            _refreshButton.Dock = DockStyle.Fill;
            _refreshButton.Click += (_, _) => LoadProcesses();
            filterRow.Controls.Add(_refreshButton, 2, 0);
            root.Controls.Add(filterRow, 0, 0);

            _processList.Dock = DockStyle.Fill;
            _processList.View = View.Details;
            _processList.CheckBoxes = true;
            _processList.FullRowSelect = true;
            _processList.GridLines = true;
            _processList.HideSelection = false;
            _processList.ShowGroups = true;
            _processList.Groups.AddRange(new[] { _applicationGroup, _backgroundGroup });
            _processList.Columns.Add("进程名", 150);
            _processList.Columns.Add("PID", 70);
            _processList.Columns.Add("窗口标题", 220);
            _processList.Columns.Add("路径", 280);
            _processList.ItemChecked += ProcessListItemChecked;
            root.Controls.Add(_processList, 0, 1);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft
            };

            _okButton.Text = "确定";
            _okButton.Width = 82;
            _okButton.Height = 30;
            _okButton.Click += (_, _) => AcceptSelection();
            AcceptButton = _okButton;
            buttons.Controls.Add(_okButton);

            var cancelButton = new Button
            {
                Text = "取消",
                Width = 82,
                Height = 30,
                DialogResult = DialogResult.Cancel
            };
            CancelButton = cancelButton;
            buttons.Controls.Add(cancelButton);
            root.Controls.Add(buttons, 0, 2);
        }

        private void LoadProcesses()
        {
            CaptureVisibleSelection();
            _processes.Clear();

            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        if (process.Id == Environment.ProcessId)
                        {
                            continue;
                        }

                        var item = TryCreateProcessListItem(process);
                        if (item is not null)
                        {
                            _processes.Add(item);
                        }
                    }
                    catch
                    {
                        // Some protected or exiting processes cannot be queried.
                    }
                }
            }

            _processes.Sort(static (left, right) =>
            {
                if (left.IsApplication != right.IsApplication)
                {
                    return left.IsApplication ? -1 : 1;
                }

                var nameCompare = string.Compare(left.ProcessName, right.ProcessName, StringComparison.OrdinalIgnoreCase);
                return nameCompare != 0 ? nameCompare : left.ProcessId.CompareTo(right.ProcessId);
            });

            ApplyFilter();
        }

        private void ApplyFilter()
        {
            var filter = _filterText.Text.Trim();
            _updatingList = true;
            _processList.BeginUpdate();
            try
            {
                _processList.Items.Clear();
                foreach (var process in _processes)
                {
                    if (!MatchesFilter(process, filter))
                    {
                        continue;
                    }

                    var listItem = new ListViewItem(process.ProcessName)
                    {
                        Checked = _checkedProcessNames.Contains(process.ProcessName),
                        Group = process.IsApplication ? _applicationGroup : _backgroundGroup,
                        Tag = process
                    };
                    listItem.SubItems.Add(process.ProcessId.ToString());
                    listItem.SubItems.Add(process.WindowTitle);
                    listItem.SubItems.Add(process.ExecutablePath);
                    _processList.Items.Add(listItem);
                }
            }
            finally
            {
                _processList.EndUpdate();
                _updatingList = false;
            }
        }

        private void ProcessListItemChecked(object? sender, ItemCheckedEventArgs eventArgs)
        {
            if (_updatingList || eventArgs.Item.Tag is not ProcessListItem process)
            {
                return;
            }

            if (eventArgs.Item.Checked)
            {
                _checkedProcessNames.Add(process.ProcessName);
            }
            else
            {
                _checkedProcessNames.Remove(process.ProcessName);
            }
        }

        private void CaptureVisibleSelection()
        {
            foreach (ListViewItem item in _processList.Items)
            {
                if (item.Tag is not ProcessListItem process)
                {
                    continue;
                }

                if (item.Checked)
                {
                    _checkedProcessNames.Add(process.ProcessName);
                }
                else
                {
                    _checkedProcessNames.Remove(process.ProcessName);
                }
            }
        }

        private void AcceptSelection()
        {
            CaptureVisibleSelection();
            if (_checkedProcessNames.Count == 0)
            {
                MessageBox.Show(this, "请选择至少一个进程。", "从进程中选择", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SelectedProcessNames = _checkedProcessNames
                .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            DialogResult = DialogResult.OK;
            Close();
        }

        private static bool MatchesFilter(ProcessListItem process, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
            {
                return true;
            }

            return process.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || process.ProcessId.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)
                || process.WindowTitle.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || process.ExecutablePath.Contains(filter, StringComparison.OrdinalIgnoreCase);
        }

        private static ProcessListItem? TryCreateProcessListItem(Process process)
        {
            try
            {
                var processName = NormalizeProcessName(process.ProcessName);
                if (string.IsNullOrWhiteSpace(processName))
                {
                    return null;
                }

                var windowTitle = SafeReadWindowTitle(process);
                return new ProcessListItem(
                    processName,
                    process.Id,
                    windowTitle,
                    SafeReadExecutablePath(process),
                    !string.IsNullOrWhiteSpace(windowTitle) || SafeHasMainWindow(process));
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizeProcessName(string processName)
        {
            var fileName = Path.GetFileName(processName.Trim());
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return "";
            }

            return fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? fileName
                : $"{fileName}.exe";
        }

        private static string SafeReadWindowTitle(Process process)
        {
            try
            {
                return process.MainWindowTitle ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static string SafeReadExecutablePath(Process process)
        {
            try
            {
                return process.MainModule?.FileName ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static bool SafeHasMainWindow(Process process)
        {
            try
            {
                return process.MainWindowHandle != IntPtr.Zero;
            }
            catch
            {
                return false;
            }
        }

        private sealed record ProcessListItem(
            string ProcessName,
            int ProcessId,
            string WindowTitle,
            string ExecutablePath,
            bool IsApplication);
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
}
