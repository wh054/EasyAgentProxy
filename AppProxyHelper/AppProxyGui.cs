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
    private const int HomeColumnsHeight = 220;
    private const int AiAppCheckRowsHeight = 88;
    private const int TargetSourceHeight = 120;
    private const int ConfigGroupHeight = 82;
    private const string ManualTargetPresetText = "手动选择";

    private readonly TextBox _configPathText = new();
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
    private readonly CheckBox _codexCheck = new();
    private readonly CheckBox _codexQqSkinCheck = new();
    private readonly CheckBox _cursorCheck = new();
    private readonly CheckBox _claudeCheck = new();
    private readonly CheckBox _antigravityCheck = new();
    private readonly CheckBox _antigravityIdeCheck = new();
    private readonly Button _createScriptsButton = new();
    private readonly Button _cliContextMenuButton = new();
    private CancellationTokenSource? _operationCts;

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

        DarkTheme.Apply(this);
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
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _operationCts?.Cancel();
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

        var tabs = DarkTheme.CreateDarkTabControl();
        tabs.Dock = DockStyle.Fill;
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
            RowCount = 3,
            Padding = new Padding(6)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 85)); // GlobalProxyGroup
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, HomeColumnsHeight)); // Columns layout
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // LogGroup
        page.Controls.Add(root);

        root.Controls.Add(BuildGlobalProxyGroup(), 0, 0);

        // Columns layout
        var columns = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0)
        };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        columns.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        columns.Controls.Add(BuildManualProxyGroup(), 0, 0);
        columns.Controls.Add(BuildAiAppProxyGroup(), 1, 0);

        root.Controls.Add(columns, 0, 1);
        root.Controls.Add(BuildLogGroup(), 0, 2);

        return page;
    }

    private Control BuildGlobalProxyGroup()
    {
        var group = new GroupBox
        {
            Text = "全局代理设置",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 6)
        };

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Padding = new Padding(6, 6, 6, 6)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, CompactRowHeight));

        table.Controls.Add(MakeLabel("代理地址"), 0, 0);

        _proxyUriText.Dock = DockStyle.Fill;
        _proxyUriText.Margin = new Padding(3, 2, 3, 2);
        table.Controls.Add(_proxyUriText, 1, 0);

        ConfigureButton(_checkButton, "检查代理");
        _checkButton.Click += async (_, _) => await RunOperationAsync("检查", RunCheckAsync);
        table.Controls.Add(_checkButton, 2, 0);

        var openLogButton = MakeButton("打开日志目录");
        openLogButton.Click += (_, _) => OpenLogDirectory();
        table.Controls.Add(openLogButton, 3, 0);

        group.Controls.Add(table);
        return group;
    }

    private Control BuildManualProxyGroup()
    {
        var group = new GroupBox
        {
            Text = "手动代理运行",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 3, 0)
        };

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 3,
            Padding = new Padding(6, 8, 6, 6)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));

        table.RowStyles.Add(new RowStyle(SizeType.Absolute, CompactRowHeight));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, CompactRowHeight));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // Row 0: Target EXE path
        table.Controls.Add(MakeLabel("目标 EXE"), 0, 0);
        _targetPathText.Dock = DockStyle.Fill;
        _targetPathText.Margin = new Padding(3, 2, 3, 2);
        table.Controls.Add(_targetPathText, 1, 0);

        var browseButton = MakeBrowseButton(() => BrowseFile(_targetPathText, "应用程序 (*.exe)|*.exe|所有文件 (*.*)|*.*"));
        table.Controls.Add(browseButton, 2, 0);

        // Row 1: Run and Stop buttons
        var buttonsPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0)
        };
        buttonsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttonsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttonsPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        ConfigureButton(_runButton, "启动代理");
        _runButton.Click += async (_, _) => await RunOperationAsync("启动", RunTargetAsync);
        buttonsPanel.Controls.Add(_runButton, 0, 0);

        ConfigureButton(_stopButton, "停止运行");
        _stopButton.Enabled = false;
        _stopButton.Click += (_, _) => _operationCts?.Cancel();
        buttonsPanel.Controls.Add(_stopButton, 1, 0);

        table.Controls.Add(buttonsPanel, 1, 1);
        table.SetColumnSpan(buttonsPanel, 2);

        // Row 2: Status label
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.Margin = new Padding(8, 0, 0, 0);
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.ForeColor = SystemColors.GrayText;
        table.Controls.Add(_statusLabel, 0, 2);
        table.SetColumnSpan(_statusLabel, 3);

        group.Controls.Add(table);
        return group;
    }

    private Control BuildAiAppProxyGroup()
    {
        var group = new GroupBox
        {
            Text = "AI 应用代理生成",
            Dock = DockStyle.Fill,
            Margin = new Padding(3, 0, 0, 0)
        };

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(6, 8, 6, 6)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, AiAppCheckRowsHeight));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, CompactRowHeight));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, CompactRowHeight));

        var checkTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            Margin = new Padding(0)
        };
        checkTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        checkTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        checkTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        checkTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        checkTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));

        ConfigureCheck(_codexCheck, "ChatGPT (Codex) 应用");
        ConfigureCheck(_codexQqSkinCheck, "Codex 启用 QQ Skin");
        ConfigureCheck(_cursorCheck, "Cursor 应用");
        ConfigureCheck(_claudeCheck, "Claude 应用");
        ConfigureCheck(_antigravityCheck, "Antigravity 应用");
        ConfigureCheck(_antigravityIdeCheck, "Antigravity IDE");

        checkTable.Controls.Add(_codexCheck, 0, 0);
        checkTable.Controls.Add(_cursorCheck, 1, 0);
        checkTable.Controls.Add(_claudeCheck, 0, 1);
        checkTable.Controls.Add(_antigravityCheck, 1, 1);
        checkTable.Controls.Add(_antigravityIdeCheck, 0, 2);
        checkTable.Controls.Add(_codexQqSkinCheck, 1, 2);
        _codexCheck.CheckedChanged += (_, _) => UpdateCodexQqSkinAvailability();

        table.Controls.Add(checkTable, 0, 0);

        ConfigureButton(_createScriptsButton, "一键生成代理启动脚本");
        _createScriptsButton.Click += (_, _) => CreateLauncherScripts();
        table.Controls.Add(_createScriptsButton, 0, 1);

        ConfigureButton(_cliContextMenuButton, "CLI 右键菜单...");
        _cliContextMenuButton.Click += (_, _) => OpenCliContextMenuDialog();
        table.Controls.Add(_cliContextMenuButton, 0, 2);

        group.Controls.Add(table);
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
            EnableCodexQqSkin = _codexQqSkinCheck.Checked,
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
        // Manual proxy target path should always default to empty.
        // Users must explicitly choose an EXE via the browse button.
        _targetPathText.Text = "";
        _targetArgumentsText.Text = FormatArguments(config.TargetArguments);
        _workingDirectoryText.Text = config.WorkingDirectory ?? "";

        _proxyUriText.Text = config.ProxyUri;
        _logDirectoryText.Text = config.LogDirectory;
        SelectCombo(_minimumLogLevelCombo, config.MinimumLogLevel);
        _captureChildOutputCheck.Checked = config.CaptureChildOutput;
        _injectProxyEnvironmentCheck.Checked = true;
        _waitForExitCheck.Checked = config.WaitForExit;
        _runDiagnosticsCheck.Checked = config.RunDiagnosticsBeforeLaunch;
        _abortOnDiagnosticsFailCheck.Checked = config.AbortLaunchWhenDiagnosticsFail;
        UpdateCodexQqSkinAvailability(config.EnableCodexQqSkin);
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

    private void CreateLauncherScripts()
    {
        try
        {
            if (!SaveConfigFromUi(showSuccess: false))
            {
                return;
            }

            var proxyUri = string.IsNullOrWhiteSpace(_proxyUriText.Text)
                ? new AppProxyConfig().ProxyUri
                : _proxyUriText.Text.Trim();
            var scripts = new List<string>();

            var checkBoxes = new[] { _codexCheck, _cursorCheck, _claudeCheck, _antigravityCheck, _antigravityIdeCheck };
            foreach (var cb in checkBoxes)
            {
                if (cb.Checked && cb.Tag is TargetApplicationPreset app)
                {
                    scripts.Add(ProxyLauncherScriptGenerator.CreateScript(
                        app.ExecutablePath,
                        app.Name,
                        proxyUri,
                        enableCodexQqSkin: app.Id.Equals("codex", StringComparison.OrdinalIgnoreCase)
                            && _codexQqSkinCheck.Checked));
                }
            }

            if (scripts.Count == 0)
            {
                MessageBox.Show(
                    this,
                    "请至少勾选一个已检测到的 AI 应用来生成代理启动脚本。",
                    "生成脚本",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            SetStatus($"已生成 {scripts.Count} 个代理启动脚本。");
            MessageBox.Show(
                this,
                "已生成代理启动脚本:\r\n\r\n" + string.Join("\r\n", scripts),
                "生成脚本",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "生成脚本失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus("生成脚本失败。");
        }
    }

    private void OpenCliContextMenuDialog()
    {
        using var dialog = new CliContextMenuDialog(_proxyUriText.Text.Trim());
        dialog.ShowDialog(this);
        SetStatus("CLI 右键菜单管理已关闭。");
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

    private void ConfigureTargetPresets()
    {
        _targetPresets.Clear();
        _targetPresets.AddRange(FindTargetApplicationPresets());

        UpdateAiAppCheckbox(_codexCheck, "codex", "ChatGPT (Codex) 应用");
        UpdateAiAppCheckbox(_cursorCheck, "cursor", "Cursor 应用");
        UpdateAiAppCheckbox(_claudeCheck, "claude", "Claude 应用");
        UpdateAiAppCheckbox(_antigravityCheck, "antigravity", "Antigravity 应用");
        UpdateAiAppCheckbox(_antigravityIdeCheck, "antigravity-ide", "Antigravity IDE");
        UpdateCodexQqSkinAvailability();
    }

    private void UpdateCodexQqSkinAvailability(bool? configuredPreference = null)
    {
        var state = ResolveCodexQqSkinOptionState(
            configuredPreference ?? _codexQqSkinCheck.Checked,
            ProxyLauncherScriptGenerator.IsCodexQqSkinInstalled(),
            _codexCheck.Enabled,
            _codexCheck.Checked);
        _codexQqSkinCheck.Text = state.Text;
        _codexQqSkinCheck.Enabled = state.Enabled;
        _codexQqSkinCheck.Checked = state.Checked;
    }

    internal static (bool Checked, bool Enabled, string Text) ResolveCodexQqSkinOptionState(
        bool configuredPreference,
        bool skinInstalled,
        bool codexAvailable,
        bool codexSelected)
    {
        return (
            Checked: configuredPreference,
            Enabled: codexAvailable && codexSelected,
            Text: skinInstalled
                ? "Codex 启用 QQ Skin (已检测到)"
                : "Codex 启用 QQ Skin (未安装，启动时仅代理)");
    }

    private void UpdateAiAppCheckbox(CheckBox checkBox, string id, string displayName)
    {
        var preset = _targetPresets.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (preset is not null)
        {
            checkBox.Text = $"{displayName} (已检测到)";
            checkBox.Enabled = true;
            checkBox.Checked = true;
            checkBox.Tag = preset;
        }
        else
        {
            checkBox.Text = $"{displayName} (未安装)";
            checkBox.Enabled = false;
            checkBox.Checked = false;
            checkBox.Tag = null;
        }
    }

    private static IReadOnlyList<TargetApplicationPreset> FindTargetApplicationPresets()
    {
        return TargetApplicationCatalog.FindInstalled();
    }

    private static Icon LoadApplicationIcon()
    {
        return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
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

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
