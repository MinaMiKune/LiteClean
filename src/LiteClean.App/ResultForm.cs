using System.Globalization;
using LiteClean.Core.Models;

namespace LiteClean.App;

internal sealed class ResultForm : Form
{
    public ResultForm(
        CleanResult result,
        string? logDirectory,
        bool logSucceeded,
        string? logError)
    {
        Text = "LiteClean 清理结果";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        ClientSize = new Size(840, 600);
        MinimumSize = new Size(700, 500);
        Font = SystemFonts.MessageBoxFont;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(16),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            Text = result.Canceled
                ? "清理已取消（已完成的删除不会恢复）"
                : result.Error is not null
                    ? "清理未执行"
                    : "清理完成",
            AutoSize = true,
            Font = new Font(
                SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif,
                15F,
                FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 10),
        };

        var summary = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 8,
            Margin = new Padding(0, 0, 0, 10),
        };
        summary.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170F));
        summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        AddSummaryRow(summary, 0, "成功删除文件", $"{result.DeletedFileCount:N0} 个");
        AddSummaryRow(summary, 1, "成功删除空目录", $"{result.DeletedDirectoryCount:N0} 个");
        AddSummaryRow(summary, 2, "实际删除文件字节", AppText.Bytes(result.DeletedFileBytes));
        AddSummaryRow(summary, 3, "跳过项目", $"{result.SkippedItemCount:N0} 个 — {AppText.JoinCounts(result.SkippedCounts)}");
        AddSummaryRow(summary, 4, "删除失败", $"{result.FailedItemCount:N0} 个 — {AppText.JoinCounts(result.FailedCounts)}");
        AddSummaryRow(summary, 5, "耗时", AppText.Duration(result.Duration));
        AddSummaryRow(summary, 6, "取消 / 未执行", $"{(result.Canceled ? "是" : "否")} / {result.NotAttemptedItemCount:N0} 个");
        AddSummaryRow(summary, 7, "清理范围", result.RootPath);

        var logLabel = new Label
        {
            Text = logError is not null
                ? $"日志：{logError}"
                : logSucceeded
                    ? $"日志已写入：{logDirectory}"
                    : "日志：未配置（不影响清理结果）",
            AutoSize = true,
            ForeColor = logError is null ? SystemColors.GrayText : Color.DarkRed,
            MaximumSize = new Size(780, 0),
            Margin = new Padding(0, 0, 0, 8),
        };

        var errorLabel = new Label
        {
            Text = result.Error ?? string.Empty,
            AutoSize = true,
            Visible = result.Error is not null,
            ForeColor = Color.DarkRed,
            MaximumSize = new Size(780, 0),
            Margin = new Padding(0, 0, 0, 6),
        };

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
        };
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var issueTitle = new Label
        {
            Text = result.Issues.Count == 0
                ? "本次没有跳过或失败的项目。"
                : result.IssuesTruncatedCount > 0
                    ? $"跳过/失败详情（仅显示前 {result.Issues.Count:N0} 条，共 {result.SkippedItemCount + result.FailedItemCount:N0} 条问题）"
                    : $"跳过/失败详情（{result.Issues.Count:N0} 条）",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 4),
        };

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoGenerateColumns = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.FixedSingle,
            GridColor = SystemColors.ControlLight,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "类型", Width = 60 });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "相对路径",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 55,
            MinimumWidth = 180,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "状态", Width = 80 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "原因", Width = 150 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Win32/HRESULT", Width = 120 });
        foreach (var issue in result.Issues)
        {
            grid.Rows.Add(
                AppText.Kind(issue.Kind),
                issue.RelativePath,
                AppText.Status(issue.Status),
                issue.Reason is null ? "-" : AppText.Reason(issue.Reason.Value),
                issue.Win32Error == 0 ? "-" : issue.Win32Error.ToString(CultureInfo.CurrentCulture));
        }

        content.Controls.Add(issueTitle, 0, 0);
        content.Controls.Add(grid, 0, 1);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = new Padding(0, 10, 0, 0),
        };
        var close = new Button { Text = "关闭", DialogResult = DialogResult.OK, AutoSize = true };
        buttons.Controls.Add(close);

        root.Controls.Add(title, 0, 0);
        root.Controls.Add(summary, 0, 1);
        root.Controls.Add(logLabel, 0, 2);
        root.Controls.Add(errorLabel, 0, 3);
        root.Controls.Add(content, 0, 4);
        root.Controls.Add(buttons, 0, 5);

        Controls.Add(root);
        AcceptButton = close;
    }

    private static void AddSummaryRow(TableLayoutPanel panel, int row, string caption, string value)
    {
        panel.Controls.Add(new Label
        {
            Text = caption,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 1, 8, 3),
        }, 0, row);
        panel.Controls.Add(new Label
        {
            Text = value,
            AutoSize = true,
            MaximumSize = new Size(620, 0),
            Margin = new Padding(0, 1, 0, 3),
        }, 1, row);
    }
}
