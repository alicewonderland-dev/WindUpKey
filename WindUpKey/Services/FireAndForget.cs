using System;
using System.Threading;
using System.Threading.Tasks;

namespace WindUpKey.Services;

/// <summary>
/// 08-P1 (K3d): a task nobody awaits, such as a send started from a UI callback or the framework tick. A fault goes to
/// <c>onFault</c> as its base exception, so it is never left unobserved; success and cancellation call nothing. The
/// task has already started on the caller's thread (no <c>Task.Run</c>); only the fault handler runs elsewhere, on the
/// thread pool or synchronously where the task completes. No Dalamud dependency.
/// </summary>
public static class FireAndForget
{
    public static void Run(Task task, Action<Exception> onFault) =>
        _ = task.ContinueWith(
            // OnlyOnFaulted: Exception is always set here (`?.` as in 07-A9's Forget).
            t => onFault(t.Exception?.GetBaseException()!),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
