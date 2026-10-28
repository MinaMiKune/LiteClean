using System.Diagnostics;

namespace LiteClean.App;

internal sealed class SettingsForm : Form
{
    private readonly NumericUpDown _hours;
    private readonly string? _logDirectory;

    public SettingsForm(int currentHours, string? logDirectory)
    {
        _logDirectory = logDirectory;
        Text = "LiteClean 设置";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        ClientSize = new Size(600, 380);
        Font = SystemFonts.MessageBoxFont;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 8,
            Padding = new Padding(18),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var protectionCaption = new Label
        {
            Text = "近期文件保护时间（小时）",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6),
        };

        _hours = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 720,
            Value = Math.Clamp(currentHours, 1, 720),
            Width = 110,
            ThousandsSeparator = false,
            Margin = new Padding(0, 0, 0, 6),
        };

        var hint = new Label
        {
            Text = "保守策略：最近修改时间在此范围内的文件会被跳过。\n" +
                   "这不是安全保证；扫描和清理时仍会逐项检查占用、权限和路径状态。",
            AutoSize = true,
            MaximumSize = new Size(540, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 16),
        };

        var logCaption = new Label
        {
            Text = "日志位置（固定目录）",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6),
        };

        var logRow = new TableLayoutPanel
        {
            AutoSize = false,
            Height = 30,
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 8),
        };
        logRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        logRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var logPathBox = new TextBox
        {
            ReadOnly = true,
            Dock = DockStyle.Fill,
            Text = logDirectory ?? "不可用（无法确定本地应用数据目录）",
            BackColor = SystemColors.Window,
            Margin = new Padding(0, 0, 8, 0),
        };
        var openLogs = new Button
        {
            Text = "打开",
            AutoSize = true,
            Enabled = !string.IsNullOrWhiteSpace(logDirectory),
            Margin = new Padding(0),
        };
        openLogs.Click += (_, _) => OpenLogs();
        logRow.Controls.Add(logPathBox, 0, 0);
        logRow.Controls.Add(openLogs, 1, 0);

        var logHint = new Label
        {
            Text = "日志仅记录汇总计数和少量错误详情（相对路径），上限约 1 MB，自动轮转 3 个文件。",
            AutoSize = true,
            MaximumSize = new Size(540, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 8),
        };

        var restoreDefault = new LinkLabel
        {
            Text = "恢复默认（24 小时）",
            AutoSize = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
            Margin = new Padding(0, 6, 0, 0),
        };
        restoreDefault.LinkClicked += (_, _) => _hours.Value = 24;

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = new Padding(0),
        };
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        layout.Controls.Add(protectionCaption, 0, 0);
        layout.Controls.Add(_hours, 0, 1);
        layout.Controls.Add(hint, 0, 2);
        layout.Controls.Add(logCaption, 0, 3);
        layout.Controls.Add(logRow, 0, 4);
        layout.Controls.Add(logHint, 0, 5);
        layout.Controls.Add(restoreDefault, 0, 6);
        layout.Controls.Add(buttons, 0, 7);

        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public int SelectedHours => (int)_hours.Value;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hours.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OpenLogs()
    {
        if (string.IsNullOrWhiteSpace(_logDirectory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_logDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_logDirectory}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or
                UnauthorizedAccessException)
        {
            MessageBox.Show(
                this,
                $"无法打开日志目录：{ex.Message}",
                "LiteClean",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }
}
