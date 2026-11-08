namespace KeelMatrix.KeyRingGuard;

public static partial class KeyRingVerifier
{
    private static Task<BoundedOperation<T>> ExecuteBoundedAsync<T>(
        Func<T> operation,
        TimeSpan timeout,
        ProviderScope providerScope,
        CancellationToken cancellationToken) =>
        ExecuteOwnedAsync(token => KeyRingOperationScheduler.Run(operation, token), timeout, providerScope, cancellationToken);

    private static async Task<BoundedOperation<T>> ExecuteOwnedAsync<T>(
        Func<CancellationToken, Task<T>> start,
        TimeSpan timeout,
        ProviderScope providerScope,
        CancellationToken cancellationToken,
        Action<T>? acceptResult = null)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new(default, null, false, true);
        }

        var lifetime = new OperationLifetime<T>(timeout, providerScope.EnterOperation(), start, acceptResult, cancellationToken);
        var task = lifetime.Operation;

        try
        {
            // Only the owned asynchronous signal wakes a stopped observer. A second
            // caller-token wait could run this continuation inline inside caller.Cancel.
            await Task.WhenAny(task, lifetime.Stopped).ConfigureAwait(false);

            var stop = lifetime.Seal();
            if (stop != StopCause.None)
            {
                return new(default, null, stop == StopCause.Deadline, stop == StopCause.Caller);
            }

            try
            {
                return new(await task.ConfigureAwait(false), null, false, false);
            }
            catch (Exception exception)
            {
                return new(default, exception, false, false);
            }
        }
        finally
        {
            ObserveFaults(lifetime.DisposeAsync().AsTask());
        }
    }

    private static async Task<T> AcceptAsync<T>(Task<T> task, Action<T>? acceptResult)
    {
        var value = await task.ConfigureAwait(false);
        acceptResult?.Invoke(value);
        return value;
    }

    private static void ObserveFaults(Task task) =>
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    // Owns the cancellation source through observer decision, actual operation completion,
    // input-relay quiescence, and the FIRST notification dispatch's completion.
    private sealed class OperationLifetime<T> : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _source = new();
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationToken _caller;
        private readonly IDisposable _lease;
        private readonly Timer _deadline;
        private readonly CancellationTokenRegistration _relay;
        private Task? _notification;
        private bool _sealed;
        private Task? _finalization;

        internal OperationLifetime(TimeSpan timeout, IDisposable lease, Func<CancellationToken, Task<T>> start, Action<T>? acceptResult, CancellationToken caller)
        {
            _caller = caller;
            _lease = lease;
            Token = _source.Token;
            // No finalizer is published until construction and operation setup finish.
            // An already-canceled caller can safely enter RequestStop during registration.
            _deadline = new Timer(static state => ((OperationLifetime<T>)state!).RequestStop(), this,
                Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _relay = caller.UnsafeRegister(static state => ((OperationLifetime<T>)state!).RequestStop(), this);
            _deadline.Change(timeout, Timeout.InfiniteTimeSpan);
            try { Operation = AcceptAsync(start(Token), acceptResult); }
            catch (Exception exception) { Operation = Task.FromException<T>(exception); }
        }

        internal Task<T> Operation { get; }
        private CancellationToken Token { get; }
        internal Task Stopped => _stopped.Task;

        internal void RequestStop()
        {
            lock (_gate)
            {
                if (_sealed || _notification is not null)
                {
                    return;
                }

                // CancelAsync sets token state synchronously but runs arbitrary registrations
                // asynchronously, outside the 32 callback slots. Later CancelAsync calls do
                // not join the first dispatch, so retain this task exactly once.
                _notification = _source.CancelAsync();
                _stopped.TrySetResult();
            }
        }

        internal StopCause Seal()
        {
            lock (_gate)
            {
                // Completion can win before the caller relay runs. Signal before selecting
                // a stop result, while the observer still owns the live source.
                var canceled = _caller.IsCancellationRequested;
                if (canceled) { RequestStop(); }
                _sealed = true;
                return _notification is null ? StopCause.None
                    : canceled ? StopCause.Caller : StopCause.Deadline;
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                _sealed = true;
                // Teardown is never awaited on the result path. Handles are fully initialized
                // and the observer cannot request another stop before finalization starts.
                return new(_finalization ??= FinalizeAsync());
            }
        }

        private async Task FinalizeAsync()
        {
            try
            {
                await _deadline.DisposeAsync().ConfigureAwait(false);
                await _relay.DisposeAsync().ConfigureAwait(false);
                try { await Operation.ConfigureAwait(false); }
                catch (Exception) { /* The observer or this owner consumes late operation faults. */ }
                if (_notification is not null)
                {
                    try { await _notification.ConfigureAwait(false); }
                    catch (Exception) { /* Notification failures never replace the stop verdict. */ }
                }
            }
            finally
            {
                _source.Dispose();
                _lease.Dispose();
            }
        }
    }

    private readonly record struct BoundedOperation<T>(T? Value, Exception? Exception, bool TimedOut, bool Canceled);

    private enum StopCause { None, Deadline, Caller }
}
