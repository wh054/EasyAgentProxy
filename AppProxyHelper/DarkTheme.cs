using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AppProxyHelper;

public static class DarkTheme
{
    public static readonly Color BackgroundDark = Color.FromArgb(30, 34, 43);      // #1e222b
    public static readonly Color PanelDark = Color.FromArgb(40, 44, 52);           // #282c34
    public static readonly Color TextLight = Color.FromArgb(171, 178, 191);        // #abb2bf
    public static readonly Color TextMuted = Color.FromArgb(130, 137, 150);        // #828996
    public static readonly Color BorderColor = Color.FromArgb(62, 68, 81);         // #3e4451
    
    public static readonly Color AccentBlue = Color.FromArgb(79, 166, 255);        // #4fa6ff
    public static readonly Color AccentRed = Color.FromArgb(224, 108, 117);        // #e06c75
    public static readonly Color AccentGreen = Color.FromArgb(152, 195, 121);      // #98c379
    public static readonly Color TextboxBg = Color.FromArgb(22, 24, 29);           // #16181d
    public static readonly Color LogBg = Color.FromArgb(13, 17, 23);               // #0d1117
    public static readonly Color LogFg = Color.FromArgb(137, 221, 255);            // #89ddff

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    public static void Apply(Form form)
    {
        ApplyImmersiveDarkMode(form.Handle);
        form.BackColor = BackgroundDark;
        form.ForeColor = TextLight;
        
        // Use modern Microsoft YaHei font as default
        form.Font = new Font("Microsoft YaHei", 9F, FontStyle.Regular);
        
        ApplyToControls(form.Controls);
    }

    private static void ApplyImmersiveDarkMode(IntPtr handle)
    {
        try
        {
            if (OperatingSystem.IsWindows() && Environment.OSVersion.Version.Major >= 10)
            {
                int useDark = 1;
                DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
            }
        }
        catch
        {
            // Fallback for non-supported Windows versions or environments
        }
    }

    private static void ApplyToControls(Control.ControlCollection controls)
    {
        foreach (Control control in controls)
        {
            if (control is TabControl tabControl)
            {
                ConfigureTabControl(tabControl);
            }
            else if (control is GroupBox groupBox)
            {
                StyleGroupBox(groupBox);
            }
            else if (control is TableLayoutPanel tableLayoutPanel)
            {
                tableLayoutPanel.BackColor = Color.Transparent;
                tableLayoutPanel.ForeColor = TextLight;
                ApplyToControls(tableLayoutPanel.Controls);
            }
            else if (control is Panel panel)
            {
                // Custom log panel styling
                if (panel.Parent is GroupBox && panel.Parent.Text == "动态日志")
                {
                    panel.BackColor = LogBg;
                }
                else
                {
                    panel.BackColor = Color.Transparent;
                }
                panel.ForeColor = TextLight;
                ApplyToControls(panel.Controls);
            }
            else if (control is Button button)
            {
                StyleButton(button);
            }
            else if (control is TextBox textBox)
            {
                if (textBox.Multiline && textBox.ReadOnly) // Log window
                {
                    textBox.BackColor = LogBg;
                    textBox.ForeColor = LogFg;
                    textBox.Font = new Font("Consolas", 9.5F, FontStyle.Regular);
                }
                else
                {
                    textBox.BackColor = TextboxBg;
                    textBox.ForeColor = TextLight;
                }
                textBox.BorderStyle = BorderStyle.FixedSingle;
            }
            else if (control is ComboBox comboBox)
            {
                comboBox.BackColor = TextboxBg;
                comboBox.ForeColor = TextLight;
                comboBox.FlatStyle = FlatStyle.Flat;
            }
            else if (control is NumericUpDown numericUpDown)
            {
                numericUpDown.BackColor = TextboxBg;
                numericUpDown.ForeColor = TextLight;
                numericUpDown.BorderStyle = BorderStyle.FixedSingle;
            }
            else if (control is CheckBox checkBox)
            {
                StyleCheckBox(checkBox);
            }
            else if (control is Label label)
            {
                if (label.ForeColor == SystemColors.GrayText)
                {
                    label.ForeColor = TextMuted;
                }
                else
                {
                    label.ForeColor = TextLight;
                }
                label.BackColor = Color.Transparent;
            }
            else if (control is MenuStrip menuStrip)
            {
                menuStrip.BackColor = BackgroundDark;
                menuStrip.ForeColor = TextLight;
                menuStrip.Renderer = new DarkMenuRenderer();
                foreach (ToolStripItem item in menuStrip.Items)
                {
                    StyleMenuItem(item);
                }
            }
        }
    }

    private static void StyleButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = BorderColor;
        button.BackColor = PanelDark;
        button.ForeColor = TextLight;
        
        var text = button.Text.Trim();
        if (text == "启动代理" || text == "启动" || text == "保存")
        {
            // Soft dark-green theme for Start
            button.BackColor = Color.FromArgb(46, 117, 89); 
            button.ForeColor = Color.FromArgb(179, 255, 222); 
            button.FlatAppearance.BorderColor = Color.FromArgb(74, 186, 142); 
        }
        else if (text == "停止" || text == "停止运行")
        {
            // Soft dark-red theme for Stop
            button.BackColor = Color.FromArgb(136, 48, 55); 
            button.ForeColor = Color.FromArgb(255, 192, 197); 
            button.FlatAppearance.BorderColor = Color.FromArgb(204, 82, 91);
        }
        else if (text == "检查代理" || text == "检查" || text == "一键生成代理启动脚本" || text == "生成脚本" || text == "打开日志目录")
        {
            // Soft dark-blue theme for check/generate/open
            button.BackColor = Color.FromArgb(38, 59, 92); 
            button.ForeColor = Color.FromArgb(163, 203, 255); 
            button.FlatAppearance.BorderColor = Color.FromArgb(79, 148, 245);
        }
    }

    private static void StyleCheckBox(CheckBox checkBox)
    {
        checkBox.FlatStyle = FlatStyle.Flat;
        checkBox.FlatAppearance.BorderSize = 1;
        checkBox.FlatAppearance.BorderColor = BorderColor;
        checkBox.BackColor = Color.Transparent;
        checkBox.ForeColor = TextLight;
        
        // Custom draw green text if application is detected
        if (checkBox.Text.Contains("已检测到"))
        {
            checkBox.ForeColor = AccentGreen;
        }
    }

    private static void StyleMenuItem(ToolStripItem item)
    {
        item.ForeColor = TextLight;
        if (item is ToolStripDropDownItem dropDownItem)
        {
            dropDownItem.DropDown.BackColor = BackgroundDark;
            dropDownItem.DropDown.ForeColor = TextLight;
            foreach (ToolStripItem subItem in dropDownItem.DropDownItems)
            {
                StyleMenuItem(subItem);
            }
        }
    }

    private static void StyleGroupBox(GroupBox groupBox)
    {
        groupBox.BackColor = BackgroundDark;
        groupBox.ForeColor = TextLight;
        ApplyToControls(groupBox.Controls);

        groupBox.Paint += (sender, e) =>
        {
            if (sender is not GroupBox gb) return;
            var rect = gb.ClientRectangle;
            
            // Clear background with main background
            e.Graphics.Clear(BackgroundDark);
            
            // Shrink slightly to avoid clipping borders
            var cardRect = new Rectangle(rect.X + 2, rect.Y + 10, rect.Width - 4, rect.Height - 12);
            
            // Fill card background
            using var bgBrush = new SolidBrush(PanelDark);
            FillRoundedRectangle(e.Graphics, bgBrush, cardRect, 6);
            
            // Draw card border
            using var borderPen = new Pen(BorderColor, 1);
            DrawRoundedRectangle(e.Graphics, borderPen, cardRect, 6);
            
            // Draw Title Text (slightly higher)
            using var textBrush = new SolidBrush(TextLight);
            using var font = new Font("Microsoft YaHei", 9F, FontStyle.Bold);
            e.Graphics.DrawString(gb.Text, font, textBrush, 12, 0);
        };
    }

    public static TabControl CreateDarkTabControl()
    {
        return new DarkTabControl();
    }

    private static void ConfigureTabControl(TabControl tabControl)
    {
        tabControl.DrawMode = TabDrawMode.OwnerDrawFixed;
        tabControl.BackColor = BackgroundDark;
        tabControl.ForeColor = TextLight;
        tabControl.Padding = new Point(16, 6);

        foreach (TabPage page in tabControl.TabPages)
        {
            page.BackColor = BackgroundDark;
            page.ForeColor = TextLight;
            page.Margin = new Padding(0);
            page.Padding = new Padding(0);
            ApplyToControls(page.Controls);
        }

        // Draw tab buttons
        tabControl.DrawItem += (sender, e) =>
        {
            if (sender is not TabControl tc) return;
            if (e.Index < 0 || e.Index >= tc.TabPages.Count) return;

            var page = tc.TabPages[e.Index];
            var isSelected = tc.SelectedIndex == e.Index;

            // Header background
            using var bgBrush = new SolidBrush(isSelected ? PanelDark : BackgroundDark);
            e.Graphics.FillRectangle(bgBrush, e.Bounds);

            // Tab border
            using var borderPen = new Pen(BorderColor);
            e.Graphics.DrawRectangle(borderPen, e.Bounds);

            // Tab text
            using var textBrush = new SolidBrush(isSelected ? TextLight : TextMuted);
            using var font = new Font("Microsoft YaHei", 9F, isSelected ? FontStyle.Bold : FontStyle.Regular);
            var stringFormat = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            e.Graphics.DrawString(page.Text, font, textBrush, e.Bounds, stringFormat);
        };

        // Draw right-side tab header background (covers the white bar)
        tabControl.Paint += (sender, e) =>
        {
            if (sender is not TabControl tc) return;
            if (tc.TabPages.Count == 0) return;

            var lastTabRect = tc.GetTabRect(tc.TabPages.Count - 1);
            var emptyRect = new Rectangle(
                lastTabRect.Right - 1, 
                0, 
                tc.Width - lastTabRect.Right + 2, 
                lastTabRect.Height + 2);
            
            using var bgBrush = new SolidBrush(BackgroundDark);
            e.Graphics.FillRectangle(bgBrush, emptyRect);

            // Bottom border line for the entire tab header area
            using var borderPen = new Pen(BorderColor);
            e.Graphics.DrawLine(borderPen, 0, lastTabRect.Bottom, tc.Width, lastTabRect.Bottom);
        };
    }

    /// <summary>
    /// Custom TabControl that suppresses the native white border around tab pages
    /// by intercepting the TCM_ADJUSTRECT message and expanding the display area.
    /// </summary>
    private class DarkTabControl : TabControl
    {
        private const int TCM_ADJUSTRECT = 0x1328;

        protected override void WndProc(ref Message m)
        {
            // Intercept TCM_ADJUSTRECT to remove the native border/padding around tab pages
            if (m.Msg == TCM_ADJUSTRECT && !DesignMode)
            {
                var rect = Marshal.PtrToStructure<RECT>(m.LParam);
                // Expand the display rectangle to cover the border
                rect.Left -= 4;
                rect.Top -= 2;
                rect.Right += 4;
                rect.Bottom += 4;
                Marshal.StructureToPtr(rect, m.LParam, false);
                return;
            }

            base.WndProc(ref m);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }

    public static void FillRoundedRectangle(Graphics g, Brush brush, Rectangle rect, int radius)
    {
        using var path = GetRoundedRectanglePath(rect, radius);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.FillPath(brush, path);
    }

    public static void DrawRoundedRectangle(Graphics g, Pen pen, Rectangle rect, int radius)
    {
        using var path = GetRoundedRectanglePath(rect, radius);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.DrawPath(pen, path);
    }

    private static System.Drawing.Drawing2D.GraphicsPath GetRoundedRectanglePath(Rectangle rect, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = radius * 2;
        var size = new Size(diameter, diameter);
        var arc = new Rectangle(rect.Location, size);

        // Top-left arc
        path.AddArc(arc, 180, 90);
        // Top-right arc
        arc.X = rect.Right - diameter;
        path.AddArc(arc, 270, 90);
        // Bottom-right arc
        arc.Y = rect.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        // Bottom-left arc
        arc.X = rect.Left;
        path.AddArc(arc, 90, 90);

        path.CloseFigure();
        return path;
    }

    // Custom MenuStrip Renderer for deep dark styling
    private class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkMenuColorTable()) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item.Selected)
            {
                var rect = new Rectangle(Point.Empty, e.Item.Size);
                using var brush = new SolidBrush(PanelDark);
                e.Graphics.FillRectangle(brush, rect);
                using var borderPen = new Pen(BorderColor);
                e.Graphics.DrawRectangle(borderPen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
            }
            else
            {
                base.OnRenderMenuItemBackground(e);
            }
        }
    }

    private class DarkMenuColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => BackgroundDark;
        public override Color MenuStripGradientBegin => BackgroundDark;
        public override Color MenuStripGradientEnd => BackgroundDark;
        public override Color MenuItemSelected => PanelDark;
        public override Color MenuItemBorder => BorderColor;
        public override Color MenuItemSelectedGradientBegin => PanelDark;
        public override Color MenuItemSelectedGradientEnd => PanelDark;
        public override Color MenuItemPressedGradientBegin => PanelDark;
        public override Color MenuItemPressedGradientEnd => PanelDark;
    }
}
