using System.Collections.Concurrent;
using System.Diagnostics;
using KeelMatrix.KeyRingGuard;
using Microsoft.AspNetCore.DataProtection;

var store = Path.Combine(Path.GetTempPath(), "keyringguard-lifecycle-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(store);
try
{
    foreach (var scenario in new[] { KeyRingScenario.RestartContinuity, KeyRingScenario.ReplicaSharing })
    {
        foreach (var cancel in new[] { false, true })
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var caller = new CancellationTokenSource();
            var disposed = 0;
            var factory = new KeyRingProviderFactory(_ => new DisposalProvider(RealProvider(), () =>
            {
                entered.Set();
                release.Wait();
                Interlocked.Increment(ref disposed);
            }), TimeSpan.FromMilliseconds(150));
            var watch = Stopwatch.StartNew();
            var task = KeyRingVerifier.VerifyAsync(scenario, factory, factory, caller.Token);
            try
            {
                Require(entered.Wait(TimeSpan.FromSeconds(5)), "Cleanup entry");
                if (cancel) { caller.Cancel(); }
                var result = await task.WaitAsync(TimeSpan.FromSeconds(3));
                Require(result.FailureKind == (cancel ? KeyRingFailureKind.Canceled : KeyRingFailureKind.None), "Cleanup result");
                Require(disposed == 0, "Blocked cleanup ownership");
                Require(result.Duration <= watch.Elapsed && (cancel || result.Duration >= TimeSpan.FromMilliseconds(100)), "Duration");
                Console.WriteLine($"PACKAGE_CLEANUP scenario={scenario} cancel={cancel} result={result.FailureKind} duration_ms={result.Duration.TotalMilliseconds:F0}");
            }
            finally
            {
                release.Set();
                await task.WaitAsync(TimeSpan.FromSeconds(3));
                Require(SpinWait.SpinUntil(() => Volatile.Read(ref disposed) == 2, TimeSpan.FromSeconds(3)), "Exactly-once cleanup");
            }
        }
    }

    foreach (var count in new[] { 31, 32, 33 })
    {
        using var release = new ManualResetEventSlim();
        var entered = 0;
        var disposed = 0;
        var created = 0;
        var threads = new ConcurrentDictionary<int, byte>();
        var factory = new KeyRingProviderFactory(_ =>
        {
            Interlocked.Increment(ref created);
            return new DisposalProvider(RealProvider(), () =>
            {
                threads.TryAdd(Environment.CurrentManagedThreadId, 0);
                Interlocked.Increment(ref entered);
                release.Wait();
                Interlocked.Increment(ref disposed);
            });
        }, TimeSpan.FromMilliseconds(300));
        var failing = new KeyRingProviderFactory((Func<CancellationToken, IDataProtectionProvider>)(_ => throw new InvalidOperationException("provider-secret")), TimeSpan.FromMilliseconds(300));
        var tasks = Enumerable.Range(0, count).Select(_ => KeyRingVerifier.VerifyAsync(KeyRingScenario.ReplicaSharing, factory, failing)).ToArray();
        try
        {
            Require(SpinWait.SpinUntil(() => Volatile.Read(ref entered) == Math.Min(count, 32), TimeSpan.FromSeconds(5)), "Capacity entry");
            var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3));
            Require(results.Count(r => r.FailureKind == KeyRingFailureKind.ProviderCreation) == Math.Min(count, 32), "Early failure count");
            Require(results.Count(r => r.FailureKind == KeyRingFailureKind.Timeout) == Math.Max(0, count - 32), "Admission timeout count");
            Require(results.All(r => !r.Message.Contains("secret", StringComparison.Ordinal)), "Sanitized diagnostics");
            if (count >= 32)
            {
                for (var repeat = 0; repeat < 8; repeat++)
                {
                    var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity,
                        new KeyRingProviderFactory((Func<CancellationToken, IDataProtectionProvider>)(_ => throw new InvalidOperationException("Must not be called")), TimeSpan.FromMilliseconds(20)));
                    Require(result.FailureKind == KeyRingFailureKind.Timeout, "Repeated saturation");
                }
            }

            Require(created == Math.Min(count, 32) && threads.Count == created, "Bounded providers and cleanup workers");
            Console.WriteLine($"PACKAGE_CAPACITY calls={count} created={created} cleanup_threads={threads.Count} repeated_saturation={(count >= 32 ? 8 : 0)}");
        }
        finally
        {
            release.Set();
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
            Require(SpinWait.SpinUntil(() => Volatile.Read(ref disposed) == Volatile.Read(ref created), TimeSpan.FromSeconds(3)), "Capacity cleanup");
        }
    }

    foreach (var asynchronous in new[] { false, true })
    {
        using var operationEntered = new ManualResetEventSlim();
        using var operationRelease = new ManualResetEventSlim();
        using var cleanupEntered = new ManualResetEventSlim();
        using var cleanupRelease = new ManualResetEventSlim();
        var disposed = 0;
        IDataProtectionProvider Create()
        {
            operationEntered.Set();
            operationRelease.Wait();
            return new DisposalProvider(RealProvider(), () =>
            {
                cleanupEntered.Set();
                cleanupRelease.Wait();
                Interlocked.Increment(ref disposed);
            });
        }

        var factory = asynchronous
            ? new KeyRingProviderFactory((Func<CancellationToken, Task<IDataProtectionProvider>>)(_ => Task.FromResult(Create())), TimeSpan.FromMilliseconds(100))
            : new KeyRingProviderFactory(_ => Create(), TimeSpan.FromMilliseconds(100));
        var task = KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory);
        try
        {
            Require(operationEntered.Wait(TimeSpan.FromSeconds(3)), "Late factory entry");
            Require((await task.WaitAsync(TimeSpan.FromSeconds(3))).FailureKind == KeyRingFailureKind.Timeout, "Late factory timeout");
            Require(!cleanupEntered.IsSet, "No premature cleanup");
            operationRelease.Set();
            Require(cleanupEntered.Wait(TimeSpan.FromSeconds(3)), "Late cleanup starts");
            Require(disposed == 0, "Late cleanup is still owned");
            Console.WriteLine($"PACKAGE_LATE async={asynchronous} result=Timeout premature_cleanup=false");
        }
        finally
        {
            operationRelease.Set();
            cleanupRelease.Set();
            await task.WaitAsync(TimeSpan.FromSeconds(3));
            Require(SpinWait.SpinUntil(() => Volatile.Read(ref disposed) == 1, TimeSpan.FromSeconds(3)), "Late exactly-once cleanup");
        }
    }

    foreach (var throwing in new[] { false, true })
    {
        var calls = 0;
        var shared = new DisposalProvider(RealProvider(), () =>
        {
            Interlocked.Increment(ref calls);
            if (throwing) { throw new InvalidOperationException("cleanup-secret"); }
        });
        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity,
            new KeyRingProviderFactory(_ => shared, TimeSpan.FromSeconds(2)));
        Require(result.FailureKind == KeyRingFailureKind.ProviderCreation && calls == 1, "Duplicate/throwing cleanup");
        Require(!result.Message.Contains("secret", StringComparison.Ordinal), "Cleanup privacy");
    }

    var recovery = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity,
        new KeyRingProviderFactory(_ => RealProvider(), TimeSpan.FromSeconds(2)));
    Require(recovery.Succeeded, "Capacity recovery");
    Console.WriteLine($"PACKAGE_LIFECYCLE: PASS assembly={typeof(KeyRingVerifier).Assembly.Location}");
}
finally
{
    Directory.Delete(store, recursive: true);
}

IDataProtectionProvider RealProvider() => DataProtectionProvider.Create(new DirectoryInfo(store), b => b.SetApplicationName("Lifecycle.App"));
static void Require(bool condition, string invariant)
{
    if (!condition) { throw new InvalidOperationException(invariant); }
}

sealed class DisposalProvider(IDataProtectionProvider inner, Action dispose) : IDataProtectionProvider, IDisposable
{
    public IDataProtector CreateProtector(string purpose) => inner.CreateProtector(purpose);
    public void Dispose() => dispose();
}
