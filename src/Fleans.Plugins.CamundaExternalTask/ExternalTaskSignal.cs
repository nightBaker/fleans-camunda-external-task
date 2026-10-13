namespace Fleans.Plugins.CamundaExternalTask;

/// <summary>
/// Wakes long-polling <c>fetchAndLock</c> requests in this process when a task may have become
/// available (queued by the stream handler, unlocked, or put back for a retry), so they do not
/// have to poll the table. Requests served by another replica fall back to a slow poll.
/// </summary>
public sealed class ExternalTaskSignal
{
    private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes on the next <see cref="Pulse"/>. Read it before checking the table.</summary>
    public Task Next => Volatile.Read(ref _next).Task;

    public void Pulse() =>
        Interlocked.Exchange(ref _next, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .TrySetResult();
}
