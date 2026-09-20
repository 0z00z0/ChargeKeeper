using ZeroZero.Update.Win32;

namespace ChargeKeeper.Services;

/// <summary>
/// One update check at a time for every surface that asks. A request while a check runs joins it
/// rather than starting a second; a request after it ended starts a fresh one, so no earlier result
/// is ever handed out. Reporting the outcome stays with each caller.
/// </summary>
/// <remarks>Kept beside the shared component's own joining because of
/// <see cref="CheckStarted"/>: a button in a window nobody clicked has no other way to learn that a
/// check is running, and every caller needs the one task object to compare against.</remarks>
internal sealed class UpdateCheckCoordinator
{
    private readonly Func<Task<UpdateFlowRun>> _check;
    private readonly object _gate = new();
    private Task<UpdateFlowRun>? _inFlight;

    internal UpdateCheckCoordinator(Func<Task<UpdateFlowRun>> check) => _check = check;

    /// <summary>Raised once per check as it starts, with the task every caller awaits, so a surface
    /// that did not ask can still show the check running.</summary>
    internal event Action<Task<UpdateFlowRun>>? CheckStarted;

    /// <summary>The running check, or a new one when none is running.</summary>
    internal Task<UpdateFlowRun> Run()
    {
        TaskCompletionSource<UpdateFlowRun> completion;
        lock (_gate)
        {
            if (_inFlight is { } running) return running;

            // Asynchronous continuations: an awaiter must never run inside this lock or inside the
            // check's own completion.
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlight  = completion.Task;
        }

        // Before the check starts, so a subscriber sees it running even when it ends at once.
        CheckStarted?.Invoke(completion.Task);
        _ = CompleteAsync(completion);
        return completion.Task;
    }

    private async Task CompleteAsync(TaskCompletionSource<UpdateFlowRun> completion)
    {
        try
        {
            var outcome = await _check().ConfigureAwait(false);
            // Released before the result is published, so an awaiter asking again starts afresh.
            Release();
            completion.SetResult(outcome);
        }
        catch (Exception ex)
        {
            Release();
            completion.SetException(ex);
        }
    }

    private void Release()
    {
        lock (_gate) _inFlight = null;
    }
}
