namespace LiteClean.Core.Workflow;

public enum WorkflowState
{
    Idle,
    Scanning,
    Ready,
    Cleaning,
    Completed,
}

public enum OperationKind
{
    Scan,
    Clean,
}

public sealed class OperationSession
{
    private CancellationTokenSource? _cts;

    internal OperationSession(long id, OperationKind kind, CancellationTokenSource cts)
    {
        Id = id;
        Kind = kind;
        _cts = cts;
    }

    public long Id { get; }
    public OperationKind Kind { get; }
    public CancellationToken Token => _cts?.Token ?? new CancellationToken(canceled: true);

    internal bool IsCompleted { get; private set; }

    internal void Complete()
    {
        if (IsCompleted)
        {
            return;
        }

        IsCompleted = true;
        _cts?.Dispose();
        _cts = null;
    }

    internal void Cancel() => _cts?.Cancel();
}

/// <summary>
/// Serialized UI/task lifecycle. It rejects re-entrant scans/cleans, only allows
/// cleaning after a completed scan, and makes cancellation + stale completion
/// handling deterministic and unit-testable without a WinForms message loop.
/// </summary>
public sealed class OperationCoordinator
{
    private readonly object _gate = new();
    private WorkflowState _state = WorkflowState.Idle;
    private OperationSession? _current;
    private long _nextId;

    public WorkflowState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                return _state is WorkflowState.Scanning or WorkflowState.Cleaning;
            }
        }
    }

    public OperationSession? TryStartScan(out string? error)
    {
        lock (_gate)
        {
            if (_state is WorkflowState.Scanning or WorkflowState.Cleaning)
            {
                error = "已有任务正在运行。";
                return null;
            }

            var cts = new CancellationTokenSource();
            var session = new OperationSession(++_nextId, OperationKind.Scan, cts);
            _current = session;
            _state = WorkflowState.Scanning;
            error = null;
            return session;
        }
    }

    public OperationSession? TryStartClean(out string? error)
    {
        lock (_gate)
        {
            if (_state != WorkflowState.Ready)
            {
                error = _state is WorkflowState.Scanning or WorkflowState.Cleaning
                    ? "已有任务正在运行。"
                    : "请先完成扫描并确认候选项目。";
                return null;
            }

            var cts = new CancellationTokenSource();
            var session = new OperationSession(++_nextId, OperationKind.Clean, cts);
            _current = session;
            _state = WorkflowState.Cleaning;
            error = null;
            return session;
        }
    }

    public bool RequestCancel(out string? error)
    {
        lock (_gate)
        {
            if (_current is null || _state is not (WorkflowState.Scanning or WorkflowState.Cleaning))
            {
                error = "当前没有可取消的任务。";
                return false;
            }

            _current.Cancel();
            error = null;
            return true;
        }
    }

    /// <summary>
    /// Completes the current operation. A stale session (for example, a late
    /// completion after an error) is ignored. Scan success -> Ready, scan
    /// failure/cancel -> Idle, clean completion -> Completed (a new scan is
    /// required before another cleanup).
    /// </summary>
    public bool Complete(OperationSession session, bool succeeded, out string? error)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            if (!ReferenceEquals(_current, session) || session.IsCompleted)
            {
                error = "任务已经结束或被新的任务替代。";
                return false;
            }

            session.Complete();
            _current = null;
            _state = session.Kind switch
            {
                OperationKind.Scan => succeeded ? WorkflowState.Ready : WorkflowState.Idle,
                OperationKind.Clean => WorkflowState.Completed,
                _ => WorkflowState.Idle,
            };
            error = null;
            return true;
        }
    }

    public bool InvalidateScanResult(out string? error)
    {
        lock (_gate)
        {
            if (_state != WorkflowState.Ready)
            {
                error = "当前没有可失效的扫描结果。";
                return false;
            }

            _state = WorkflowState.Idle;
            error = null;
            return true;
        }
    }
}
