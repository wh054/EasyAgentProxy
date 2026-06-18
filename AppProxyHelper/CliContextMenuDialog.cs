using System.Text;

namespace AppProxyHelper;

internal sealed class CliContextMenuDialog : Form
{
    private const int DialogWidth = 680;
    private const int DialogHeight = 460;
    private const int RowHeight = 36;

    private readonly TextBox _proxyUriText = new();
    private readonly CheckBox _claudeCheck = new();
    private readonly CheckBox _codexCheck = new();
    private readonly TextBox _statusText = new();

    public CliContextMenuDialog(string proxyUri)
    {
        Text = "CLI 右键菜单";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        Size = new Size(DialogWidth, DialogHeight);
        MinimumSize = new Size(DialogWidth, DialogHeight);
        MaximumSize = new Size(DialogWidth, DialogHeight);
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;

        BuildUi();
        _proxyUriText.Text = string.IsNullOrWhiteSpace(proxyUri)
            ? new AppProxyConfig().ProxyUri
            : proxyUri;
        RefreshStatus();

        DarkTheme.Apply(this);
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(10)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, RowHeight));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, RowHeight));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, RowHeight + 8));
        Controls.Add(root);

        root.Controls.Add(BuildProxyRow(), 0, 0);
        root.Controls.Add(BuildToolRow(), 0, 1);
        root.Controls.Add(BuildStatusBox(), 0, 2);
        root.Controls.Add(BuildButtonRow(), 0, 3);
    }

    private Control BuildProxyRow()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 4)
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        row.Controls.Add(MakeLabel("代理地址"), 0, 0);
        _proxyUriText.Dock = DockStyle.Fill;
        _proxyUriText.Margin = new Padding(3, 2, 0, 2);
        row.Controls.Add(_proxyUriText, 1, 0);
        return row;
    }

    private Control BuildToolRow()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 4)
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        row.Controls.Add(MakeLabel("CLI 工具"), 0, 0);

        ConfigureCheck(_claudeCheck, "Claude Code CLI");
        _claudeCheck.Checked = true;
        row.Controls.Add(_claudeCheck, 1, 0);

        ConfigureCheck(_codexCheck, "Codex CLI");
        _codexCheck.Checked = true;
        row.Controls.Add(_codexCheck, 2, 0);
        return row;
    }

    private Control BuildStatusBox()
    {
        _statusText.Dock = DockStyle.Fill;
        _statusText.Multiline = true;
        _statusText.ReadOnly = true;
        _statusText.ScrollBars = ScrollBars.Vertical;
        _statusText.Font = new Font(FontFamily.GenericMonospace, 9);
        _statusText.Margin = new Padding(0, 4, 0, 6);
        return _statusText;
    }

    private Control BuildButtonRow()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Margin = new Padding(0)
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));

        var refreshButton = MakeButton("刷新状态");
        refreshButton.Click += (_, _) => RefreshStatus();
        row.Controls.Add(refreshButton, 0, 0);

        var installButton = MakeButton("安装/更新右键菜单");
        installButton.Click += (_, _) => InstallSelectedTools();
        row.Controls.Add(installButton, 1, 0);

        var uninstallButton = MakeButton("卸载右键菜单");
        uninstallButton.Click += (_, _) => UninstallSelectedTools();
        row.Controls.Add(uninstallButton, 2, 0);

        var closeButton = MakeButton("关闭");
        closeButton.Click += (_, _) => Close();
        row.Controls.Add(closeButton, 3, 0);

        AcceptButton = installButton;
        CancelButton = closeButton;
        return row;
    }

    private void InstallSelectedTools()
    {
        var tools = GetSelectedTools();
        if (tools.Count == 0)
        {
            MessageBox.Show(this, "请至少选择一个 CLI 工具。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            var proxyUri = string.IsNullOrWhiteSpace(_proxyUriText.Text)
                ? new AppProxyConfig().ProxyUri
                : _proxyUriText.Text.Trim();
            var scripts = ExplorerContextMenuManager.Install(tools, proxyUri);
            RefreshStatus();
            MessageBox.Show(
                this,
                "已安装/更新 CLI 右键菜单:\r\n\r\n" + string.Join("\r\n", scripts),
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "安装右键菜单失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UninstallSelectedTools()
    {
        var tools = GetSelectedTools();
        if (tools.Count == 0)
        {
            MessageBox.Show(this, "请至少选择一个 CLI 工具。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ExplorerContextMenuManager.Uninstall(tools);
        RefreshStatus();
        MessageBox.Show(this, "已卸载所选 CLI 右键菜单。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void RefreshStatus()
    {
        var statuses = ExplorerContextMenuManager.GetStatuses(CliToolCatalog.All);
        var builder = new StringBuilder();
        foreach (var status in statuses)
        {
            builder.AppendLine(status.Tool.DisplayName);
            builder.AppendLine($"  右键菜单: {(status.IsInstalled ? "已安装" : "未安装")}");
            builder.AppendLine($"  命令检测: {(status.CommandAvailable ? "已找到 " : "未找到 ")}{status.Tool.CommandName}");
            builder.AppendLine($"  脚本路径: {status.ScriptPath}");
            builder.AppendLine();
        }

        _statusText.Text = builder.ToString().TrimEnd();
    }

    private IReadOnlyList<CliToolDefinition> GetSelectedTools()
    {
        var selected = new List<CliToolDefinition>();
        foreach (var tool in CliToolCatalog.All)
        {
            if (tool.Id.Equals("claude-code", StringComparison.OrdinalIgnoreCase) && _claudeCheck.Checked
                || tool.Id.Equals("codex-cli", StringComparison.OrdinalIgnoreCase) && _codexCheck.Checked)
            {
                selected.Add(tool);
            }
        }

        return selected;
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
            Margin = new Padding(2),
            MinimumSize = new Size(0, 32),
            TextAlign = ContentAlignment.MiddleCenter,
            AutoEllipsis = true
        };
    }

    private static void ConfigureCheck(CheckBox checkBox, string text)
    {
        checkBox.Text = text;
        checkBox.Dock = DockStyle.Fill;
        checkBox.Margin = new Padding(3, 2, 3, 2);
        checkBox.TextAlign = ContentAlignment.MiddleLeft;
        checkBox.AutoEllipsis = true;
    }
}
