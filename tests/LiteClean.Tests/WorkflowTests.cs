using LiteClean.Core.Models;
using LiteClean.Core.Workflow;

namespace LiteClean.Tests;

internal sealed class WorkflowTests
{
    [Test("Only one concurrent scan can start")]
    public void OnlyOneConcurrentScanStarts()
    {
        var coordinator = new OperationCoordinator();
        var sessions = new List<OperationSession>();
        var failures = 0;
        Parallel.For(0, 64, index =>
        {
            var session = coordinator.TryStartScan(out _);
            if (session is null)
            {
                Interlocked.Increment(ref failures);
            }
            else
            {
                lock (sessions)
                {
                    sessions.Add(session);
                }
            }
        });

        Check.Equal(1, sessions.Count);
        Check.Equal(63, failures);
        Check.Equal(WorkflowState.Scanning, coordinator.State);
        Check.True(coordinator.IsBusy);
        coordinator.Complete(sessions[0], succeeded: false, out _);
        Check.Equal(WorkflowState.Idle, coordinator.State);
    }

    [Test("Clean requires a completed scan and cannot re-enter")]
    public void CleanRequiresReadyState()
    {
        var coordinator = new OperationCoordinator();
        Check.True(coordinator.TryStartClean(out _) is null);
        Check.Equal(WorkflowState.Idle, coordinator.State);

        var scan = coordinator.TryStartScan(out _);
        Check.True(scan is not null);
        Check.True(coordinator.TryStartClean(out _) is null);

        Check.True(coordinator.Complete(scan!, succeeded: true, out _));
        Check.Equal(WorkflowState.Ready, coordinator.State);

        var clean = coordinator.TryStartClean(out _);
        Check.True(clean is not null);
        Check.Equal(WorkflowState.Cleaning, coordinator.State);
        Check.True(coordinator.TryStartClean(out _) is null);
        Check.True(coordinator.TryStartScan(out _) is null);

        Check.True(coordinator.Complete(clean!, succeeded: true, out _));
        Check.Equal(WorkflowState.Completed, coordinator.State);
        Check.True(coordinator.TryStartClean(out _) is null);

        var rescanned = coordinator.TryStartScan(out _);
        Check.True(rescanned is not null);
        Check.Equal(WorkflowState.Scanning, coordinator.State);
        coordinator.Complete(rescanned!, succeeded: true, out _);
        Check.Equal(WorkflowState.Ready, coordinator.State);
    }

    [Test("Cancel only works while busy and is idempotent")]
    public void CancelBehavior()
    {
        var coordinator = new OperationCoordinator();
        Check.False(coordinator.RequestCancel(out _));

        var scan = coordinator.TryStartScan(out _)!;
        Check.True(coordinator.RequestCancel(out _));
        Check.True(scan.Token.IsCancellationRequested);
        Check.True(coordinator.RequestCancel(out _));
        Check.True(scan.Token.IsCancellationRequested);

        Check.True(coordinator.Complete(scan, succeeded: false, out _));
        Check.Equal(WorkflowState.Idle, coordinator.State);
        Check.False(coordinator.RequestCancel(out _));
    }

    [Test("Stale completions are ignored")]
    public void StaleCompletionIsIgnored()
    {
        var coordinator = new OperationCoordinator();
        var first = coordinator.TryStartScan(out _)!;
        Check.True(coordinator.Complete(first, succeeded: false, out _));
        Check.False(coordinator.Complete(first, succeeded: true, out _));
        Check.Equal(WorkflowState.Idle, coordinator.State);

        var second = coordinator.TryStartScan(out _)!;
        Check.False(coordinator.Complete(first, succeeded: true, out _));
        Check.Equal(WorkflowState.Scanning, coordinator.State);
        Check.True(coordinator.Complete(second, succeeded: false, out _));
        Check.Equal(WorkflowState.Idle, coordinator.State);
    }

    [Test("Scan failure returns to idle; invalidation returns ready to idle")]
    public void StateTransitions()
    {
        var coordinator = new OperationCoordinator();
        var scan = coordinator.TryStartScan(out _)!;
        Check.True(coordinator.Complete(scan, succeeded: false, out _));
        Check.Equal(WorkflowState.Idle, coordinator.State);

        var second = coordinator.TryStartScan(out _)!;
        Check.True(coordinator.Complete(second, succeeded: true, out _));
        Check.Equal(WorkflowState.Ready, coordinator.State);
        Check.True(coordinator.InvalidateScanResult(out _));
        Check.Equal(WorkflowState.Idle, coordinator.State);
        Check.False(coordinator.InvalidateScanResult(out _));
    }

    [Test("Canceling one operation does not affect the next")]
    public void CancelIsolation()
    {
        var coordinator = new OperationCoordinator();
        var first = coordinator.TryStartScan(out _)!;
        coordinator.RequestCancel(out _);
        coordinator.Complete(first, succeeded: false, out _);

        var second = coordinator.TryStartScan(out _)!;
        Check.False(second.Token.IsCancellationRequested);
        Check.True(coordinator.Complete(second, succeeded: true, out _));
        Check.Equal(WorkflowState.Ready, coordinator.State);
    }
}
