using Microsoft.AspNetCore.DataProtection;

namespace KeelMatrix.KeyRingGuard;

public static partial class KeyRingVerifier
{
    private sealed class ProviderScope : IDisposable
    {
        // Reserve ownership before creating providers, not after callbacks return. A blocked
        // operation or cleanup retains its reservation, bounding retained cleanup batches too.
        private static readonly SemaphoreSlim Lifecycles = new(32, 32);
        private readonly object _gate = new();
        private readonly HashSet<IDisposable> _providers = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<IDataProtectionProvider, Task<bool>> _retirements = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<OperationLease> _operations = [];
        private readonly TaskCompletionSource _cleanupCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeOperations;
        private bool _disposed;

        private ProviderScope() { }

        internal static async Task<ProviderScope> CreateAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (!await Lifecycles.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
            {
                throw new TimeoutException();
            }

            return new ProviderScope();
        }

        internal async Task CompleteAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            Dispose();
            lock (_gate)
            {
                // A late operation still owns the provider. Do not add another timeout
                // to observation of an operation that already timed out or was canceled.
                if (_activeOperations != 0)
                {
                    return;
                }
            }

            try
            {
                await _cleanupCompletion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (TimeoutException)
            {
                // Cleanup is best effort and does not replace the scenario verdict.
                // The reservation remains held until cleanup actually finishes.
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        internal IDisposable EnterOperation()
        {
            lock (_gate)
            {
                _activeOperations++;
                var lease = new OperationLease(this);
                _operations.Add(lease);
                return lease;
            }
        }

        internal Task CaptureQuiescence()
        {
            lock (_gate) { return Task.WhenAll(_operations.Select(operation => operation.Completion)); }
        }

        internal Task<bool> RetireAsync(IDataProtectionProvider provider)
        {
            lock (_gate)
            {
                if (_retirements.TryGetValue(provider, out var existing)) { return existing; }
                if (provider is not IDisposable disposable)
                {
                    return _retirements[provider] = Task.FromResult(true);
                }

                _providers.Remove(disposable);
                // Once transferred, teardown cannot be canceled or retried by final cleanup.
                return _retirements[provider] = KeyRingOperationScheduler.Run(() =>
                {
                    disposable.Dispose();
                    return true;
                }, CancellationToken.None);
            }
        }

        internal void Add(IDataProtectionProvider provider)
        {
            if (provider is not IDisposable disposable)
            {
                return;
            }

            lock (_gate)
            {
                // Add is always called under an active operation lease, including
                // a factory that returns after the caller has stopped waiting.
                if (!_retirements.ContainsKey(provider)) { _providers.Add(disposable); }
            }
        }

        public void Dispose()
        {
            IDisposable[]? providers = null;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                if (_activeOperations == 0)
                {
                    providers = TakeProviders();
                }
            }

            StartCleanup(providers);
        }

        private void ExitOperation(OperationLease operation)
        {
            IDisposable[]? providers = null;
            lock (_gate)
            {
                _activeOperations--;
                _operations.Remove(operation);
                if (_disposed && _activeOperations == 0)
                {
                    providers = TakeProviders();
                }
            }

            StartCleanup(providers);
        }

        private IDisposable[] TakeProviders()
        {
            var providers = _providers.ToArray();
            _providers.Clear();
            return providers;
        }

        private void StartCleanup(IDisposable[]? providers)
        {
            if (providers is null)
            {
                return;
            }

            _ = CleanupAsync(providers);
        }

        private async Task CleanupAsync(IDisposable[] providers)
        {
            try
            {
                if (providers.Length != 0)
                {
                    // One batch per reserved lifecycle. Use the same callback slots
                    // and re-entry guard as provider operations, without canceling
                    // cleanup of providers whose ownership we already accepted.
                    await KeyRingOperationScheduler.Run(() =>
                    {
                        for (var index = providers.Length - 1; index >= 0; index--)
                        {
                            DisposeProvider(providers[index]);
                        }

                        return true;
                    }, CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                Lifecycles.Release();
                _cleanupCompletion.TrySetResult();
            }
        }

        private static void DisposeProvider(IDisposable disposable)
        {
            try
            {
                disposable.Dispose();
            }
            catch
            {
                // Disposal is best effort; provider operation failures remain the verification result.
            }
        }

        private sealed class OperationLease : IDisposable
        {
            private ProviderScope? _owner;
            private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal Task Completion => _completion.Task;

            internal OperationLease(ProviderScope owner) => _owner = owner;

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner is null) { return; }
                owner.ExitOperation(this);
                _completion.TrySetResult();
            }
        }
    }
}
