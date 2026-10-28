using System.Diagnostics;
using System.Globalization;
using LiteClean.Core.Cleaning;
using LiteClean.Core.Configuration;
using LiteClean.Core.Logging;
using LiteClean.Core.Models;
using LiteClean.Core.Scanning;
using LiteClean.Core.Security;
using LiteClean.Core.Workflow;

namespace LiteClean.App;

internal sealed class MainForm : Form
{
    private readonly OperationCoordinator _workflow = new();
    private readonly AppPaths? _paths;
    private readonly SettingsStore? _settingsStore;
    private readonly CleanupLogger? _logger;
    private readonly string? _startupWarning;
    private readonly ToolTip _scopeToolTip = new();
    private AppSettings _settings;

    private PathBoundary? _boundary;
    private ScanResult? _scan;
    private IReadOnlyList<ScanCandidate> _candidates = Array.Empty<ScanCandidate>();
    private readonly HashSet<string> _deselectedPaths = new(StringComparer.OrdinalIgnoreCase);
    private int _selectedFileCount;
    private int _selectedDirectoryCount;
    private long _selectedBytes;
    private bool _cancelRequested;
    private Task? _activeTask;
    private int _operationGeneration;
    private bool _rootCheckInProgress;
    private bool _closing;
    private bool _allowClose;
    private bool _closePromptShown;
    private CleanResult? _lastCleanResult;
    private LogWriteResult? _lastLogResult;

    private Label _scopeLabel = null!;
    private Label _protectionLabel = null!;
    private Label _statusLabel = null!;
    private ProgressBar _progressBar = null!;
    private Label _bytesValue = null!;
    private Label _filesValue = null!;
    private Label _dirsValue = null!;
    private Label _recentValue = null!;
    private Label _otherValue = null!;
    private Label _bytesCaption = null!;
    private Label _filesCaption = null!;
    private Label _dirsCaption = null!;
    private Label _recentCaption = null!;
    private Label _otherCaption = null!;
    private Button _scanButton = null!;
    private Button _cleanButton = null!;
    private Button _cancelButton = null!;
    private Button _settingsButton = null!;
    private Button _viewResultButton = null!;
    private Button _openLogsButton = null!;
    private LinkLabel _selectAllLink = null!;
    private LinkLabel _selectNoneLink = null!;
    private TabControl _tabs = null!;
    private TabPage _candidateTab = null!;
    private TabPage _skipTab = null!;
    private DataGridView _candidateGrid = null!;
    private DataGridView _skipGrid = null!;
    private Label _skipSummary = null!;

    public MainForm()
    {
        Text = AppInfo.Title;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        ClientSize = new Size(1120, 780);
        MinimumSize = new Size(980, 680);
        Font = SystemFonts.MessageBoxFont;
        KeyPreview = true;

        if (AppPaths.TryCreateDefault(out var paths, out var pathError))
        {
            var resolvedPaths = paths!;
            _paths = resolvedPaths;
            var store = new SettingsStore(resolvedPaths.SettingsFilePath);
            _settingsStore = store;
            var loaded = store.Load();
            _settings = loaded.Settings;
            _startupWarning = loaded.Warning;
            try
            {
                _logger = new CleanupLogger(resolvedPaths.LogDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
            {
                _logger = null;
                _startupWarning = $"日志初始化失败：{ex.Message}";
            }
        }
        else
        {
            _settings = AppSettings.Default;
            _startupWarning = pathError;
        }

        InitializeComponent();
        Shown += MainForm_Shown;
        FormClosing += MainForm_FormClosing;
    }

    private void InitializeComponent()
    {
        SuspendLayout();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(14),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        root.Controls.Add(CreateHeader(), 0, 0);
        root.Controls.Add(CreateStats(), 0, 1);
        root.Controls.Add(CreateProgress(), 0, 2);
        root.Controls.Add(CreateActions(), 0, 3);
        root.Controls.Add(CreateTabs(), 0, 4);
        Controls.Add(root);

        UpdateProtectionLabel();
        SetStatus("正在准备…");
        WireGridEvents();
        UpdateButtons();
        ResumeLayout(performLayout: true);
    }

    private Control CreateHeader()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 10),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var textPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 4,
            Margin = new Padding(0),
        };
        textPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        textPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        textPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        textPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            Text = AppInfo.Name,
            AutoSize = true,
            Font = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif, 20F, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 0),
        };
        var subtitle = new Label
        {
            Text = $"v{AppInfo.Version} · 仅清理当前用户临时目录（TEMP） · 扫描只读 · 永久删除 · 无后台驻留",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 2, 0, 6),
        };
        _scopeLabel = new Label
        {
            Text = "清理范围：正在检查 TEMP…",
            AutoSize = false,
            Dock = DockStyle.Fill,
            Height = 22,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, 0, 2),
        };
        _protectionLabel = new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 0),
        };

        textPanel.Controls.Add(title, 0, 0);
        textPanel.Controls.Add(subtitle, 0, 1);
        textPanel.Controls.Add(_scopeLabel, 0, 2);
        textPanel.Controls.Add(_protectionLabel, 0, 3);

        _settingsButton = new Button
        {
            Text = "设置",
            AutoSize = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Margin = new Padding(12, 4, 0, 0),
        };
        _settingsButton.Click += SettingsButton_Click;

        header.Controls.Add(textPanel, 0, 0);
        header.Controls.Add(_settingsButton, 1, 0);
        return header;
    }

    private Control CreateStats()
    {
        var stats = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 5,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 8),
        };
        for (var i = 0; i < 5; i++)
        {
            stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20F));
        }

        stats.Controls.Add(
            CreateStatCard("预计可释放空间", out _bytesValue, out _bytesCaption),
            0,
            0);
        stats.Controls.Add(
            CreateStatCard("可清理文件", out _filesValue, out _filesCaption),
            1,
            0);
        stats.Controls.Add(
            CreateStatCard("候选目录", out _dirsValue, out _dirsCaption),
            2,
            0);
        stats.Controls.Add(
            CreateStatCard("近期保护·跳过", out _recentValue, out _recentCaption),
            3,
            0);
        stats.Controls.Add(
            CreateStatCard("占用/权限/其他", out _otherValue, out _otherCaption),
            4,
            0);
        return stats;
    }

    private static Control CreateStatCard(string caption, out Label value, out Label captionLabel)
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            Height = 82,
            Margin = new Padding(0, 0, 8, 0),
            Padding = new Padding(10, 6, 10, 6),
            BackColor = SystemColors.Window,
            BorderStyle = BorderStyle.FixedSingle,
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 58F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 42F));

        value = new Label
        {
            Text = "—",
            Dock = DockStyle.Fill,
            Font = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif, 13F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = new Padding(0),
        };
        captionLabel = new Label
        {
            Text = caption,
            Dock = DockStyle.Fill,
            ForeColor = SystemColors.GrayText,
            TextAlign = ContentAlignment.TopLeft,
            AutoEllipsis = true,
            Margin = new Padding(0),
        };
        layout.Controls.Add(value, 0, 0);
        layout.Controls.Add(captionLabel, 0, 1);
        panel.Controls.Add(layout);
        return panel;
    }

    private Control CreateProgress()
    {
        var progress = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 2,
            Margin = new Padding(0, 0, 0, 8),
        };
        progress.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        progress.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        progress.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        progress.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _statusLabel = new Label
        {
            Text = string.Empty,
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = 22,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, 0, 4),
        };
        _cancelButton = new Button
        {
            Text = "取消扫描",
            AutoSize = true,
            Enabled = false,
            Margin = new Padding(10, 0, 0, 4),
        };
        _cancelButton.Click += CancelButton_Click;

        _progressBar = new ProgressBar
        {
            Dock = DockStyle.Fill,
            Height = 17,
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 0,
            Margin = new Padding(0),
        };

        progress.Controls.Add(_statusLabel, 0, 0);
        progress.Controls.Add(_cancelButton, 1, 0);
        progress.SetColumnSpan(_progressBar, 2);
        progress.Controls.Add(_progressBar, 0, 1);
        return progress;
    }

    private Control CreateActions()
    {
        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 8),
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var left = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0),
        };
        _scanButton = new Button { Text = "开始扫描", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
        _cleanButton = new Button
        {
            Text = "清理选中项",
            AutoSize = true,
            Enabled = false,
            Margin = new Padding(0, 0, 8, 0),
        };
        _viewResultButton = new Button
        {
            Text = "查看本次结果",
            AutoSize = true,
            Enabled = false,
            Margin = new Padding(0, 0, 8, 0),
        };
        _scanButton.Click += ScanButton_Click;
        _cleanButton.Click += CleanButton_Click;
        _viewResultButton.Click += (_, _) => ShowLastResult();
        left.Controls.Add(_scanButton);
        left.Controls.Add(_cleanButton);
        left.Controls.Add(_viewResultButton);

        _selectAllLink = new LinkLabel
        {
            Text = "全选",
            AutoSize = true,
            Enabled = false,
            Margin = new Padding(8, 8, 6, 0),
        };
        _selectNoneLink = new LinkLabel
        {
            Text = "全不选",
            AutoSize = true,
            Enabled = false,
            Margin = new Padding(0, 8, 0, 0),
        };
        _selectAllLink.LinkClicked += (_, _) => SelectAllCandidates();
        _selectNoneLink.LinkClicked += (_, _) => SelectNoCandidates();
        left.Controls.Add(_selectAllLink);
        left.Controls.Add(_selectNoneLink);

        _openLogsButton = new Button
        {
            Text = "打开日志目录",
            AutoSize = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Margin = new Padding(8, 0, 0, 0),
        };
        _openLogsButton.Click += (_, _) => OpenLogDirectory();

        actions.Controls.Add(left, 0, 0);
        actions.Controls.Add(_openLogsButton, 1, 0);
        return actions;
    }

    private Control CreateTabs()
    {
        _tabs = new TabControl { Dock = DockStyle.Fill, Margin = new Padding(0) };

        _candidateTab = new TabPage("可清理项");
        _candidateTab.Padding = new Padding(6);
        var candidateLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        candidateLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        candidateLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        var candidateHint = new Label
        {
            Text = "默认全选。取消勾选的项目不会被删除；清理后仍会逐项复核路径、状态和占用情况。",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 6),
        };

        _candidateGrid = CreateCandidateGrid();
        candidateLayout.Controls.Add(candidateHint, 0, 0);
        candidateLayout.Controls.Add(_candidateGrid, 0, 1);
        _candidateTab.Controls.Add(candidateLayout);

        _skipTab = new TabPage("跳过/异常");
        _skipTab.Padding = new Padding(6);
        var skipLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        skipLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        skipLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _skipSummary = new Label
        {
            Text = "尚未扫描。",
            AutoSize = false,
            Dock = DockStyle.Fill,
            Height = 54,
            TextAlign = ContentAlignment.TopLeft,
            Margin = new Padding(0, 0, 0, 6),
        };
        _skipGrid = CreateSkipGrid();
        skipLayout.Controls.Add(_skipSummary, 0, 0);
        skipLayout.Controls.Add(_skipGrid, 0, 1);
        _skipTab.Controls.Add(skipLayout);

        _tabs.TabPages.Add(_candidateTab);
        _tabs.TabPages.Add(_skipTab);
        return _tabs;
    }

    private static DataGridView CreateCandidateGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            VirtualMode = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoGenerateColumns = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            ReadOnly = true,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.FixedSingle,
            GridColor = SystemColors.ControlLight,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            RowTemplate = { Height = 24 },
        };
        grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            HeaderText = "选择",
            Width = 50,
            ReadOnly = true,
            ThreeState = false,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "类型", Width = 60 });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "相对路径",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 60,
            MinimumWidth = 220,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "大小",
            Width = 100,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight },
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "修改时间", Width = 145 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "状态", Width = 90 });
        return grid;
    }

    private static DataGridView CreateSkipGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            VirtualMode = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoGenerateColumns = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            ReadOnly = true,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.FixedSingle,
            GridColor = SystemColors.ControlLight,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            RowTemplate = { Height = 22 },
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "类型", Width = 60 });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "相对路径",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 65,
            MinimumWidth = 220,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "跳过原因", Width = 180 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "代码", Width = 110 });
        return grid;
    }

    private void WireGridEvents()
    {
        _candidateGrid.CellValueNeeded += CandidateGrid_CellValueNeeded;
        _candidateGrid.CellClick += CandidateGrid_CellClick;
        _candidateGrid.KeyDown += CandidateGrid_KeyDown;
        _skipGrid.CellValueNeeded += SkipGrid_CellValueNeeded;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        UpdateButtons();
    }

    private async void MainForm_Shown(object? sender, EventArgs e)
    {
        await CheckRootAsync(showDialog: false);
        if (!_closing && _startupWarning is not null && _boundary is not null)
        {
            SetStatus(_startupWarning, isWarning: true);
        }
    }

    private async Task CheckRootAsync(bool showDialog)
    {
        if (_rootCheckInProgress)
        {
            return;
        }

        _rootCheckInProgress = true;
        UpdateButtons();
        SetStatus("正在检查 TEMP 环境…");
        try
        {
            var resolution = await Task.Run(ResolveTempRoot);
            if (_closing)
            {
                return;
            }

            ApplyRootResolution(resolution, showDialog);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            if (!_closing)
            {
                _boundary = null;
                _scopeLabel.Text = "清理范围：TEMP 检查失败";
                SetStatus($"检查 TEMP 失败：{ex.Message}", isWarning: true);
            }
        }
        finally
        {
            _rootCheckInProgress = false;
            if (!_closing)
            {
                UpdateButtons();
            }
        }
    }

    private void ApplyRootResolution(RootResolution resolution, bool showDialog)
    {
        if (resolution.Boundary is not null)
        {
            _boundary = resolution.Boundary;
            _scopeLabel.Text = $"清理范围：{_boundary.RootPath}";
            _scopeLabel.ForeColor = SystemColors.ControlText;
            _scopeToolTip.SetToolTip(_scopeLabel, _boundary.RootPath);
            SetStatus("TEMP 已确认。点击“开始扫描”生成清理预览。");
            return;
        }

        _boundary = null;
        _scopeLabel.Text = "清理范围：不可用";
        _scopeLabel.ForeColor = Color.DarkRed;
        var message = resolution.Failure?.Message ?? "TEMP 未通过安全校验。";
        SetStatus(message, isWarning: true);
        if (showDialog && !_closing)
        {
            MessageBox.Show(
                this,
                $"{message}\n\nLiteClean 不会回退到 TMP、当前目录或磁盘根目录。",
                "无法确定清理范围",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private async void ScanButton_Click(object? sender, EventArgs e)
    {
        if (_workflow.IsBusy || _rootCheckInProgress)
        {
            return;
        }

        var session = _workflow.TryStartScan(out var startError);
        if (session is null)
        {
            SetStatus(startError ?? "无法开始扫描。", isWarning: true);
            return;
        }

        var succeeded = false;
        var generation = ++_operationGeneration;
        _cancelRequested = false;
        UpdateButtons();
        try
        {
            ClearScanResults();
            SetCaptionsForScan();
            SetStatus("正在解析 TEMP…");
            SetProgress(marquee: true, maximum: 0, value: 0);

            var resolveTask = Task.Run(ResolveTempRoot);
            _activeTask = resolveTask;
            var resolution = await resolveTask;
            if (_closing)
            {
                return;
            }

            ApplyRootResolution(resolution, showDialog: true);
            if (resolution.Boundary is null)
            {
                SetStatus("扫描未开始：TEMP 未通过安全校验。", isWarning: true);
                SetProgress(marquee: false, maximum: 0, value: 0);
                return;
            }

            _boundary = resolution.Boundary;
            SetStatus("正在扫描临时目录（只读）…");
            SetProgress(marquee: true, maximum: 0, value: 0);
            var options = new ScanOptions
            {
                RecentProtection = TimeSpan.FromHours(_settings.RecentFileProtectionHours),
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 4),
            };
            var scanner = new TempScanner(options);
            var progress = new Progress<ScanProgress>(value =>
            {
                if (!_closing && generation == _operationGeneration)
                {
                    UpdateScanProgress(value);
                }
            });

            var scanTask = scanner.ScanAsync(_boundary, progress, session.Token);
            _activeTask = scanTask;
            var result = await scanTask;
            if (_closing)
            {
                return;
            }

            if (result.Canceled || !result.Completed)
            {
                ClearScanResults();
                SetStatus("扫描已取消，未删除任何文件。", isWarning: true);
                SetProgress(marquee: false, maximum: 0, value: 0);
                return;
            }

            ApplyScanResult(result);
            succeeded = true;
            SetStatus(
                result.Candidates.Count == 0
                    ? "扫描完成：没有符合安全条件的可清理项目。"
                    : "扫描完成。请检查预览，确认后点击“清理选中项”。",
                isWarning: result.Candidates.Count == 0);
            SetProgress(marquee: false, maximum: 0, value: 0);
        }
        catch (OperationCanceledException)
        {
            if (!_closing)
            {
                ClearScanResults();
                SetStatus("扫描已取消，未删除任何文件。", isWarning: true);
                SetProgress(marquee: false, maximum: 0, value: 0);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            if (!_closing)
            {
                ClearScanResults();
                SetStatus($"扫描失败：{ex.Message}", isWarning: true);
                SetProgress(marquee: false, maximum: 0, value: 0);
                MessageBox.Show(
                    this,
                    $"扫描失败：{ex.Message}\n\n未执行任何删除操作。",
                    "LiteClean",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
        finally
        {
            _operationGeneration++;
            _cancelRequested = false;
            _activeTask = null;
            _workflow.Complete(session, succeeded, out _);
            if (!_closing)
            {
                UpdateButtons();
            }
        }
    }

    private async void CleanButton_Click(object? sender, EventArgs e)
    {
        if (_workflow.State != WorkflowState.Ready || _scan is null || _boundary is null)
        {
            SetStatus("请先完成扫描。", isWarning: true);
            return;
        }

        var selected = GetSelectedCandidates();
        if (selected.Count == 0)
        {
            MessageBox.Show(
                this,
                "没有选中任何项目。",
                "LiteClean",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var confirmation = BuildConfirmationText(selected);
        var answer = MessageBox.Show(
            this,
            confirmation,
            "确认清理",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        var session = _workflow.TryStartClean(out var startError);
        if (session is null)
        {
            SetStatus(startError ?? "无法开始清理。", isWarning: true);
            return;
        }

        var generation = ++_operationGeneration;
        var completed = false;
        _cancelRequested = false;
        UpdateButtons();
        try
        {
            SetCaptionsForCleanPending();
            SetStatus("正在清理…");
            SetProgress(marquee: false, maximum: selected.Count, value: 0);
            var options = new CleanOptions
            {
                RecentProtection = TimeSpan.FromHours(_settings.RecentFileProtectionHours),
            };
            var cleaner = new TempCleaner(options);
            var progress = new Progress<CleanProgress>(value =>
            {
                if (!_closing && generation == _operationGeneration)
                {
                    UpdateCleanProgress(value);
                }
            });

            var cleanTask = cleaner.CleanAsync(_boundary, selected, progress, session.Token);
            _activeTask = cleanTask;
            var result = await cleanTask;
            if (_closing)
            {
                return;
            }

            _lastCleanResult = result;
            _lastLogResult = WriteCleanLog(result);
            ApplyCleanResult(result);
            completed = result.Completed;
            ShowLastResult();
        }
        catch (OperationCanceledException)
        {
            if (!_closing)
            {
                SetStatus("清理已取消；已完成的删除不会恢复。", isWarning: true);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            if (!_closing)
            {
                SetStatus($"清理失败：{ex.Message}", isWarning: true);
                MessageBox.Show(
                    this,
                    $"清理过程中出现错误：{ex.Message}\n\nLiteClean 已停止后续删除。请重新扫描后再试。",
                    "LiteClean",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
        finally
        {
            _operationGeneration++;
            _cancelRequested = false;
            _activeTask = null;
            _workflow.Complete(session, completed, out _);
            if (!_closing)
            {
                UpdateButtons();
            }
        }
    }

    private void ApplyScanResult(ScanResult result)
    {
        _scan = result;
        _candidates = result.Candidates;
        _deselectedPaths.Clear();
        _selectedFileCount = result.CandidateFileCount;
        _selectedDirectoryCount = result.CandidateDirectoryCount;
        _selectedBytes = result.EstimatedFileBytes;
        _lastCleanResult = null;
        _lastLogResult = null;

        _candidateGrid.RowCount = 0;
        _candidateGrid.RowCount = _candidates.Count;
        _candidateGrid.Invalidate();
        _skipGrid.RowCount = 0;
        _skipGrid.RowCount = result.SkippedPreview.Count;
        _skipGrid.Invalidate();

        _skipSummary.Text = BuildSkipSummary(result);
        SetCaptionsForScan();
        UpdateSelectionSummary();
        _tabs.SelectedTab = _candidates.Count > 0 ? _candidateTab : _skipTab;
        _progressBar.Value = 0;
    }

    private void ClearScanResults()
    {
        _scan = null;
        _candidates = Array.Empty<ScanCandidate>();
        _deselectedPaths.Clear();
        _selectedFileCount = 0;
        _selectedDirectoryCount = 0;
        _selectedBytes = 0;
        _lastCleanResult = null;
        _lastLogResult = null;
        if (_candidateGrid is not null)
        {
            _candidateGrid.RowCount = 0;
            _skipGrid.RowCount = 0;
            _skipSummary.Text = "尚未扫描。";
        }

        SetCaptionsForScan();
        _bytesValue.Text = "—";
        _filesValue.Text = "—";
        _dirsValue.Text = "—";
        _recentValue.Text = "—";
        _otherValue.Text = "—";
        UpdateButtons();
    }

    private void SetCaptionsForScan()
    {
        _bytesCaption.Text = "预计可释放空间";
        _filesCaption.Text = "可清理文件";
        _dirsCaption.Text = "候选目录";
        _recentCaption.Text = "近期保护·跳过";
        _otherCaption.Text = "占用/权限/其他";
    }

    private void SetCaptionsForCleanPending()
    {
        _bytesCaption.Text = "预计可释放空间";
        _filesCaption.Text = "待清理文件";
        _dirsCaption.Text = "待清理目录";
    }

    private void UpdateScanProgress(ScanProgress progress)
    {
        _bytesValue.Text = AppText.Bytes(progress.CandidateFileBytes);
        _filesValue.Text = $"{AppText.Count(progress.CandidateFiles)} 个";
        _dirsValue.Text = $"{AppText.Count(progress.CandidateDirectories)} 个";
        _otherValue.Text = $"{AppText.Count(progress.SkippedItems)} 个";
        _recentValue.Text = "—";
        SetStatus(
            $"正在扫描：已检查 {AppText.Count((int)Math.Min(progress.EntriesVisited, int.MaxValue))} 项 · " +
            $"候选文件 {AppText.Count(progress.CandidateFiles)} · 跳过 {AppText.Count(progress.SkippedItems)}");
    }

    private void UpdateCleanProgress(CleanProgress progress)
    {
        if (progress.TotalItems > 0)
        {
            _progressBar.Maximum = progress.TotalItems;
            _progressBar.Value = Math.Clamp(progress.AttemptedItems, 0, progress.TotalItems);
        }

        SetStatus(
            $"正在清理：{AppText.Count(progress.AttemptedItems)} / {AppText.Count(progress.TotalItems)} 项 · " +
            $"已删除文件 {AppText.Count(progress.DeletedFiles)} · 空目录 {AppText.Count(progress.DeletedDirectories)} · " +
            $"实际 {AppText.Bytes(progress.DeletedBytes)}");
    }

    private void ApplyCleanResult(CleanResult result)
    {
        _bytesCaption.Text = "实际删除文件字节";
        _filesCaption.Text = "成功删除文件";
        _dirsCaption.Text = "成功删除空目录";
        _recentCaption.Text = "跳过项目";
        _otherCaption.Text = "删除失败";
        _bytesValue.Text = AppText.Bytes(result.DeletedFileBytes);
        _filesValue.Text = $"{AppText.Count(result.DeletedFileCount)} 个";
        _dirsValue.Text = $"{AppText.Count(result.DeletedDirectoryCount)} 个";
        _recentValue.Text = $"{AppText.Count(result.SkippedItemCount)} 个";
        _otherValue.Text = $"{AppText.Count(result.FailedItemCount)} 个";
        _progressBar.Style = ProgressBarStyle.Continuous;
        _progressBar.Maximum = Math.Max(1, result.TotalSelectedItemCount);
        _progressBar.Value = Math.Clamp(result.AttemptedItemCount, 0, _progressBar.Maximum);

        if (result.Error is not null)
        {
            SetStatus($"清理未执行：{result.Error}", isWarning: true);
        }
        else if (result.Canceled)
        {
            SetStatus("清理已取消；已完成的删除不会恢复。请重新扫描后再次清理。", isWarning: true);
        }
        else
        {
            SetStatus(
                $"清理完成：删除 {AppText.Count(result.DeletedFileCount)} 个文件、" +
                $"{AppText.Count(result.DeletedDirectoryCount)} 个空目录，" +
                $"实际 {AppText.Bytes(result.DeletedFileBytes)}。如需再次清理请重新扫描。");
        }
    }

    private LogWriteResult? WriteCleanLog(CleanResult result)
    {
        if (_logger is null)
        {
            return null;
        }

        return _logger.LogCleanSession(result, _scan, _settings, AppInfo.Version, result.Error);
    }

    private void ShowLastResult()
    {
        if (_lastCleanResult is null || _closing)
        {
            return;
        }

        using var dialog = new ResultForm(
            _lastCleanResult,
            _paths?.LogDirectory,
            _lastLogResult?.Success ?? false,
            _lastLogResult?.Error);
        dialog.ShowDialog(this);
    }

    private void UpdateSelectionSummary()
    {
        if (_scan is null)
        {
            return;
        }

        var totalFiles = _scan.CandidateFileCount;
        var totalDirectories = _scan.CandidateDirectoryCount;
        _bytesValue.Text = AppText.Bytes(_selectedBytes);
        _filesValue.Text = _selectedFileCount == totalFiles
            ? $"{AppText.Count(totalFiles)} 个"
            : $"{AppText.Count(_selectedFileCount)} / {AppText.Count(totalFiles)}";
        _dirsValue.Text = _selectedDirectoryCount == totalDirectories
            ? $"{AppText.Count(totalDirectories)} 个"
            : $"{AppText.Count(_selectedDirectoryCount)} / {AppText.Count(totalDirectories)}";
        _recentValue.Text = $"{AppText.Count(_scan.RecentSkippedCount)} 个";
        _otherValue.Text = $"{AppText.Count(_scan.SkippedItemCount - _scan.RecentSkippedCount)} 个";
    }

    private IReadOnlyList<ScanCandidate> GetSelectedCandidates()
    {
        if (_deselectedPaths.Count == 0)
        {
            return _candidates.ToArray();
        }

        return _candidates
            .Where(candidate => !_deselectedPaths.Contains(candidate.FullPath))
            .ToArray();
    }

    private void SelectAllCandidates()
    {
        if (_scan is null || _workflow.State != WorkflowState.Ready)
        {
            return;
        }

        _deselectedPaths.Clear();
        _selectedFileCount = _scan.CandidateFileCount;
        _selectedDirectoryCount = _scan.CandidateDirectoryCount;
        _selectedBytes = _scan.EstimatedFileBytes;
        _candidateGrid.Invalidate();
        UpdateSelectionSummary();
        UpdateButtons();
    }

    private void SelectNoCandidates()
    {
        if (_scan is null || _workflow.State != WorkflowState.Ready)
        {
            return;
        }

        foreach (var candidate in _candidates)
        {
            _deselectedPaths.Add(candidate.FullPath);
        }

        _selectedFileCount = 0;
        _selectedDirectoryCount = 0;
        _selectedBytes = 0;
        _candidateGrid.Invalidate();
        UpdateSelectionSummary();
        UpdateButtons();
    }

    private void ToggleCandidate(int rowIndex)
    {
        if (_scan is null || _workflow.State != WorkflowState.Ready || _closing)
        {
            return;
        }

        if (rowIndex < 0 || rowIndex >= _candidates.Count)
        {
            return;
        }

        var candidate = _candidates[rowIndex];
        var wasSelected = !_deselectedPaths.Contains(candidate.FullPath);
        if (wasSelected)
        {
            _deselectedPaths.Add(candidate.FullPath);
            if (candidate.Kind == ItemKind.File)
            {
                _selectedFileCount = Math.Max(0, _selectedFileCount - 1);
                _selectedBytes = Math.Max(0, _selectedBytes - candidate.SizeBytes);
            }
            else
            {
                _selectedDirectoryCount = Math.Max(0, _selectedDirectoryCount - 1);
            }
        }
        else
        {
            _deselectedPaths.Remove(candidate.FullPath);
            if (candidate.Kind == ItemKind.File)
            {
                _selectedFileCount++;
                _selectedBytes += candidate.SizeBytes;
            }
            else
            {
                _selectedDirectoryCount++;
            }
        }

        _candidateGrid.InvalidateCell(0, rowIndex);
        UpdateSelectionSummary();
        UpdateButtons();
    }

    private void CandidateGrid_CellValueNeeded(object? sender, DataGridViewCellValueEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _candidates.Count)
        {
            return;
        }

        var candidate = _candidates[e.RowIndex];
        e.Value = e.ColumnIndex switch
        {
            0 => !_deselectedPaths.Contains(candidate.FullPath),
            1 => AppText.Kind(candidate.Kind),
            2 => candidate.RelativePath,
            3 => candidate.Kind == ItemKind.File ? AppText.Bytes(candidate.SizeBytes) : "—",
            4 => candidate.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture),
            5 => "可清理",
            _ => string.Empty,
        };
    }

    private void SkipGrid_CellValueNeeded(object? sender, DataGridViewCellValueEventArgs e)
    {
        if (_scan is null || e.RowIndex < 0 || e.RowIndex >= _scan.SkippedPreview.Count)
        {
            return;
        }

        var item = _scan.SkippedPreview[e.RowIndex];
        e.Value = e.ColumnIndex switch
        {
            0 => AppText.Kind(item.Kind),
            1 => item.RelativePath,
            2 => AppText.Reason(item.Reason),
            3 => item.Win32Error == 0 ? "—" : item.Win32Error.ToString(CultureInfo.CurrentCulture),
            _ => string.Empty,
        };
    }

    private void CandidateGrid_CellClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.ColumnIndex == 0)
        {
            ToggleCandidate(e.RowIndex);
        }
    }

    private void CandidateGrid_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space && _candidateGrid.CurrentCell is not null)
        {
            ToggleCandidate(_candidateGrid.CurrentCell.RowIndex);
            e.Handled = true;
        }
    }

    private void CancelButton_Click(object? sender, EventArgs e)
    {
        if (!_workflow.RequestCancel(out var error))
        {
            SetStatus(error ?? "当前没有可取消的任务。", isWarning: true);
            return;
        }

        _cancelRequested = true;
        _cancelButton.Enabled = false;
        SetStatus(
            _workflow.State == WorkflowState.Scanning
                ? "正在取消扫描…"
                : "正在取消清理；当前已开始的项目会完成，之后不再启动新的删除。",
            isWarning: true);
        UpdateButtons();
    }

    private void SettingsButton_Click(object? sender, EventArgs e)
    {
        if (_workflow.IsBusy)
        {
            return;
        }

        using var dialog = new SettingsForm(
            _settings.RecentFileProtectionHours,
            _paths?.LogDirectory);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var hours = dialog.SelectedHours;
        if (hours == _settings.RecentFileProtectionHours)
        {
            return;
        }

        var newSettings = new AppSettings { RecentFileProtectionHours = hours };
        if (_settingsStore is not null)
        {
            var save = _settingsStore.Save(newSettings);
            if (!save.Success)
            {
                MessageBox.Show(
                    this,
                    save.Error ?? "保存设置失败。",
                    "LiteClean",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
        }

        _settings = newSettings;
        UpdateProtectionLabel();
        if (_workflow.InvalidateScanResult(out _))
        {
            ClearScanResults();
            SetStatus("近期文件保护时间已更新，请重新扫描。");
        }
        else
        {
            SetStatus("设置已更新。");
        }

        UpdateButtons();
    }

    private void UpdateProtectionLabel()
    {
        _protectionLabel.Text =
            $"近期文件保护：{_settings.RecentFileProtectionHours} 小时（保守跳过，不是安全保证） · " +
            "扫描只读 · 清理需明确确认";
    }

    private void OpenLogDirectory()
    {
        if (_paths is null)
        {
            MessageBox.Show(
                this,
                "日志目录不可用（无法确定本地应用数据目录）。",
                "LiteClean",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            Directory.CreateDirectory(_paths.LogDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_paths.LogDirectory}\"")
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

    private void UpdateButtons()
    {
        var busy = _workflow.IsBusy;
        var ready = _workflow.State == WorkflowState.Ready;
        var hasSelection = _selectedFileCount + _selectedDirectoryCount > 0;
        _scanButton.Enabled = !busy && !_rootCheckInProgress;
        _cleanButton.Enabled = !busy && ready && hasSelection;
        _cancelButton.Enabled = busy && !_cancelRequested;
        _cancelButton.Text = _workflow.State == WorkflowState.Cleaning ? "取消清理" : "取消扫描";
        _settingsButton.Enabled = !busy;
        _selectAllLink.Enabled = !busy && ready && _candidates.Count > 0;
        _selectNoneLink.Enabled = !busy && ready && _candidates.Count > 0;
        _viewResultButton.Enabled = !busy && _lastCleanResult is not null;
        _openLogsButton.Enabled = _paths is not null;
        _candidateGrid.Enabled = !busy;
        _skipGrid.Enabled = !busy;
    }

    private void SetStatus(string text, bool isWarning = false)
    {
        if (_statusLabel is null)
        {
            return;
        }

        _statusLabel.Text = text.Replace('\r', ' ').Replace('\n', ' ');
        _statusLabel.ForeColor = isWarning ? Color.DarkRed : SystemColors.ControlText;
    }

    private void SetProgress(bool marquee, int maximum, int value)
    {
        if (marquee)
        {
            _progressBar.Style = ProgressBarStyle.Marquee;
            _progressBar.MarqueeAnimationSpeed = 30;
            return;
        }

        _progressBar.MarqueeAnimationSpeed = 0;
        _progressBar.Style = ProgressBarStyle.Continuous;
        _progressBar.Maximum = Math.Max(1, maximum);
        _progressBar.Value = Math.Clamp(value, 0, _progressBar.Maximum);
    }

    private static RootResolution ResolveTempRoot()
    {
        return TempRootResolver.TryResolveFromEnvironment(out var boundary, out var failure)
            ? new RootResolution(boundary, null)
            : new RootResolution(null, failure);
    }

    private string BuildConfirmationText(IReadOnlyList<ScanCandidate> selected)
    {
        var scan = _scan!;
        var selectedFiles = selected.Count(candidate => candidate.Kind == ItemKind.File);
        var selectedDirectories = selected.Count(candidate => candidate.Kind == ItemKind.Directory);
        var selectedBytes = selected
            .Where(candidate => candidate.Kind == ItemKind.File)
            .Sum(candidate => candidate.SizeBytes);
        var skipped = scan.SkippedItemCount;
        var recent = scan.RecentSkippedCount;
        var deselected = _candidates.Count - selected.Count;
        var text = new System.Text.StringBuilder();
        text.AppendLine("即将永久删除以下临时项目：");
        text.AppendLine($"· 文件：{selectedFiles:N0} 个（逻辑大小约 {AppText.Bytes(selectedBytes)}）");
        text.AppendLine($"· 目录候选：{selectedDirectories:N0} 个（仅删除清理后为空的目录）");
        text.AppendLine($"· 清理范围：{scan.RootPath}");
        text.AppendLine($"· 未选/已跳过：{deselected:N0} 个未选；扫描共跳过 {skipped:N0} 个（近期修改 {recent:N0} 个）");
        text.AppendLine($"· 近期保护：{_settings.RecentFileProtectionHours} 小时");
        text.AppendLine();
        text.AppendLine("正在使用、权限不足、只读、近期修改、链接或扫描后状态变化的项目会被跳过。");
        text.AppendLine("删除不进入回收站，也无法撤销。是否开始清理？");
        return text.ToString();
    }

    private static string BuildSkipSummary(ScanResult result)
    {
        if (result.SkippedItemCount == 0)
        {
            return "本次扫描没有跳过项目。";
        }

        var lines = string.Join(
            "  ·  ",
            result.SkippedCounts
                .OrderByDescending(static pair => pair.Value)
                .ThenBy(static pair => pair.Key)
                .Select(pair => $"{AppText.Reason(pair.Key)}：{AppText.Count(pair.Value)}"));
        if (result.SkippedPreviewTruncatedCount > 0)
        {
            lines += $"  ·  预览仅显示前 {result.SkippedPreview.Count:N0} 条（共 {result.SkippedItemCount:N0} 条）";
        }

        return lines;
    }

    private async void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        if (!_workflow.IsBusy)
        {
            _closing = true;
            return;
        }

        if (e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing)
        {
            _workflow.RequestCancel(out _);
            return;
        }

        e.Cancel = true;
        if (_closePromptShown)
        {
            return;
        }

        _closePromptShown = true;
        var answer = MessageBox.Show(
            this,
            "扫描或清理正在运行。退出会请求取消任务；已经完成的删除不会恢复。\n\n确定退出吗？",
            "退出 LiteClean",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
        {
            _closePromptShown = false;
            return;
        }

        _closing = true;
        _operationGeneration++;
        _workflow.RequestCancel(out _);
        _cancelRequested = true;
        SetStatus("正在取消并等待任务结束…", isWarning: true);
        UpdateButtons();
        var task = _activeTask;
        if (task is not null)
        {
            try
            {
                await task;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                Debug.WriteLine(ex);
            }
        }

        _allowClose = true;
        Close();
    }

    private sealed record RootResolution(PathBoundary? Boundary, PathBoundaryFailure? Failure);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _scopeToolTip.Dispose();
        }

        base.Dispose(disposing);
    }
}
