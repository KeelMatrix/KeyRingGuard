using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.DataProtection;

namespace KeelMatrix.KeyRingGuard.UnitTests;

[Collection("Provider lifecycle")]
public sealed class ProviderCleanupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockedCleanupDoesNotPreventResultOrCancellation(bool cancel)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var caller = new CancellationTokenSource();
        var disposed = 0;
        KeyRingProviderFactory Factory() => new(_ => new FakeProvider(dispose: () =>
        {
            entered.Set();
            release.Wait();
            Interlocked.Increment(ref disposed);
        }), TimeSpan.FromMilliseconds(150));
        var watch = Stopwatch.StartNew();
        var task = KeyRingVerifier.VerifyAsync(KeyRingScenario.ReplicaSharing, Factory(), Factory(), caller.Token);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            if (cancel)
            {
                caller.Cancel();
            }

            var result = await task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(cancel ? KeyRingFailureKind.Canceled : KeyRingFailureKind.None, result.FailureKind);
            Assert.Equal(0, Volatile.Read(ref disposed));
            Assert.InRange(result.Duration, TimeSpan.Zero, watch.Elapsed);
            if (!cancel)
            {
                Assert.True(result.Duration >= TimeSpan.FromMilliseconds(100));
            }
        }
        finally
        {
            release.Set();
            await task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref disposed) == 2, TimeSpan.FromSeconds(3)));
        }
    }

    [Theory]
    [InlineData("factory", "fast")]
    [InlineData("factory", "throw")]
    [InlineData("factory", "block")]
    [InlineData("protector", "fast")]
    [InlineData("protector", "throw")]
    [InlineData("protector", "block")]
    [InlineData("protect", "fast")]
    [InlineData("protect", "throw")]
    [InlineData("protect", "block")]
    [InlineData("unprotect", "fast")]
    [InlineData("unprotect", "throw")]
    [InlineData("unprotect", "block")]
    [InlineData("cancel", "fast")]
    [InlineData("cancel", "throw")]
    [InlineData("cancel", "block")]
    [InlineData("duplicate", "fast")]
    [InlineData("duplicate", "throw")]
    [InlineData("duplicate", "block")]
    public async Task CleanupPreservesEarlyExitVerdictsAndDeduplicates(string exit, string cleanup)
    {
        using var release = new ManualResetEventSlim();
        using var caller = new CancellationTokenSource();
        var disposed = 0;
        var calls = 0;
        var first = new FakeProvider(
            createProtector: exit == "protector" ? _ => throw new InvalidOperationException("secret") : null,
            protect: value =>
            {
                if (exit == "protect") { throw new InvalidOperationException("secret"); }
                if (exit == "cancel") { caller.Cancel(); }
                return value;
            },
            unprotect: exit == "unprotect" ? _ => throw new InvalidOperationException("secret") : null,
            dispose: Dispose);
        var factory = new KeyRingProviderFactory(_ =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 2 && exit == "factory") { throw new InvalidOperationException("secret"); }
            return call == 1 || exit == "duplicate" ? first : new FakeProvider(dispose: Dispose);
        }, TimeSpan.FromMilliseconds(100));
        var task = KeyRingVerifier.VerifyAsync(KeyRingScenario.ReplicaSharing, factory, factory, caller.Token);
        try
        {
            var result = await task.WaitAsync(TimeSpan.FromSeconds(3));
            var expected = exit switch
            {
                "factory" or "duplicate" => KeyRingFailureKind.ProviderCreation,
                "protector" or "protect" => KeyRingFailureKind.Protect,
                "unprotect" => KeyRingFailureKind.Unprotect,
                _ => KeyRingFailureKind.Canceled
            };
            Assert.Equal(expected, result.FailureKind);
            Assert.DoesNotContain("secret", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            release.Set();
            await task.WaitAsync(TimeSpan.FromSeconds(3));
            var expectedCount = exit is "factory" or "duplicate" ? 1 : 2;
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref disposed) == expectedCount, TimeSpan.FromSeconds(3)));
        }

        void Dispose()
        {
            if (cleanup == "block") { release.Wait(); }
            Interlocked.Increment(ref disposed);
            if (cleanup == "throw") { throw new InvalidOperationException("cleanup-secret"); }
        }
    }

    [Fact]
    public async Task ZeroAndFourProviderPathsRetainTheirOwnershipRules()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var created = 0;
        var disposed = 0;
        var boundary = new KeyRingBoundary();
        KeyRingProviderFactory Factory() => new(_ =>
        {
            Interlocked.Increment(ref created);
            return new FakeProvider(dispose: () => Interlocked.Increment(ref disposed));
        }, TimeSpan.FromSeconds(1), null, boundary);
        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, Factory(), cancellationToken: canceled.Token);
        Assert.Equal(KeyRingFailureKind.Canceled, result.FailureKind);
        Assert.Equal(0, created);
        result = await KeyRingVerifier.VerifyWithIsolationControlAsync(KeyRingScenario.ApplicationIsolation,
            Factory(), Factory(), new KeyRingIsolationControl(Factory(), Factory()), CancellationToken.None);
        Assert.Equal(KeyRingFailureKind.UnexpectedCrossUnprotect, result.FailureKind);
        Assert.Equal(4, created);
        Assert.Equal(4, disposed);
    }

    [Theory]
    [InlineData("sync-factory", false)]
    [InlineData("async-factory", false)]
    [InlineData("protect", false)]
    [InlineData("sync-factory", true)]
    [InlineData("async-factory", true)]
    [InlineData("protect", true)]
    public async Task LateCallbacksRetainProvidersUntilUseAndCleanupFinish(string callback, bool cancel)
    {
        using var operationEntered = new ManualResetEventSlim();
        using var operationRelease = new ManualResetEventSlim();
        using var cleanupEntered = new ManualResetEventSlim();
        using var cleanupRelease = new ManualResetEventSlim();
        using var caller = new CancellationTokenSource();
        var disposed = 0;
        var premature = 0;
        var provider = new FakeProvider(protect: value =>
        {
            if (callback == "protect") { BlockOperation(); }
            return value;
        }, dispose: () =>
        {
            cleanupEntered.Set();
            cleanupRelease.Wait();
            Interlocked.Increment(ref disposed);
        });
        KeyRingProviderFactory factory = callback == "async-factory"
            ? new((Func<CancellationToken, Task<IDataProtectionProvider>>)(_ =>
                { BlockOperation(); return Task.FromResult<IDataProtectionProvider>(provider); }), TimeSpan.FromMilliseconds(100))
            : new(_ => { if (callback == "sync-factory") { BlockOperation(); } return provider; }, TimeSpan.FromMilliseconds(100));
        var task = KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory, cancellationToken: caller.Token);
        try
        {
            Assert.True(operationEntered.Wait(TimeSpan.FromSeconds(3)));
            if (cancel) { caller.Cancel(); }
            var result = await task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(cancel ? KeyRingFailureKind.Canceled : KeyRingFailureKind.Timeout, result.FailureKind);
            Assert.False(cleanupEntered.IsSet);
            operationRelease.Set();
            Assert.True(cleanupEntered.Wait(TimeSpan.FromSeconds(3)));
            Assert.Equal(0, premature);
            Assert.Equal(0, disposed);
        }
        finally
        {
            operationRelease.Set();
            cleanupRelease.Set();
            await task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref disposed) == 1, TimeSpan.FromSeconds(3)));
        }

        void BlockOperation()
        {
            operationEntered.Set();
            operationRelease.Wait();
            if (Volatile.Read(ref disposed) != 0) { Interlocked.Increment(ref premature); }
        }
    }

    [Theory]
    [InlineData(31, false)]
    [InlineData(32, false)]
    [InlineData(33, false)]
    [InlineData(31, true)]
    [InlineData(32, true)]
    [InlineData(33, true)]
    public async Task BlockedCleanupRetainsBoundedAdmissionIncludingLateOperations(int count, bool late)
    {
        using var operationRelease = new ManualResetEventSlim();
        using var cleanupRelease = new ManualResetEventSlim();
        var created = 0;
        var entered = 0;
        var disposed = 0;
        var threads = new ConcurrentDictionary<int, byte>();
        var factory = new KeyRingProviderFactory(_ =>
        {
            Interlocked.Increment(ref created);
            if (late) { operationRelease.Wait(CancellationToken.None); }
            return new FakeProvider(dispose: () =>
            {
                threads.TryAdd(Environment.CurrentManagedThreadId, 0);
                Interlocked.Increment(ref entered);
                cleanupRelease.Wait();
                Interlocked.Increment(ref disposed);
            });
        }, TimeSpan.FromMilliseconds(250));
        var failing = new KeyRingProviderFactory((Func<CancellationToken, IDataProtectionProvider>)(_ => throw new InvalidOperationException("secret")), TimeSpan.FromMilliseconds(250));
        var tasks = Enumerable.Range(0, count).Select(_ => KeyRingVerifier.VerifyAsync(KeyRingScenario.ReplicaSharing, factory, failing)).ToArray();
        try
        {
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref created) == Math.Min(count, 32), TimeSpan.FromSeconds(3)));
            var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(4));
            Assert.Equal(late ? count : Math.Max(0, count - 32), results.Count(r => r.FailureKind == KeyRingFailureKind.Timeout));
            Assert.Equal(late ? 0 : Math.Min(count, 32), results.Count(r => r.FailureKind == KeyRingFailureKind.ProviderCreation));
            operationRelease.Set();
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref entered) == Math.Min(count, 32), TimeSpan.FromSeconds(3)));
            Assert.Equal(Math.Min(count, 32), threads.Count);
            if (count >= 32)
            {
                var extraCalls = 0;
                var extra = new KeyRingProviderFactory(_ =>
                {
                    Interlocked.Increment(ref extraCalls);
                    return new FakeProvider();
                }, TimeSpan.FromMilliseconds(20));
                for (var repeat = 0; repeat < 8; repeat++)
                {
                    Assert.Equal(KeyRingFailureKind.Timeout, (await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, extra)).FailureKind);
                }

                using var caller = new CancellationTokenSource();
                var queued = KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, extra, cancellationToken: caller.Token);
                caller.Cancel();
                Assert.Equal(KeyRingFailureKind.Canceled, (await queued.WaitAsync(TimeSpan.FromSeconds(2))).FailureKind);
                Assert.Equal(0, extraCalls);
                Assert.Equal(32, entered);
            }
        }
        finally
        {
            operationRelease.Set();
            cleanupRelease.Set();
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(4));
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref disposed) == Volatile.Read(ref created), TimeSpan.FromSeconds(3)));
        }

        var followups = await Task.WhenAll(Enumerable.Range(0, 33).Select(_ => KeyRingVerifier.VerifyAsync(
            KeyRingScenario.RestartContinuity, new KeyRingProviderFactory(_ => new FakeProvider(), TimeSpan.FromSeconds(2)))));
        Assert.All(followups, result => Assert.True(result.Succeeded, result.Message));
    }

    [Fact]
    public async Task CleanupUsesTheCallbackReentryGuard()
    {
        var nested = new ConcurrentBag<KeyRingVerificationResult>();
        var nestedCalls = 0;
        var nestedFactory = new KeyRingProviderFactory(_ =>
        {
            Interlocked.Increment(ref nestedCalls);
            return new FakeProvider();
        }, TimeSpan.FromSeconds(1));
        var factory = new KeyRingProviderFactory(_ => new FakeProvider(dispose: () => nested.Add(
            KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, nestedFactory).GetAwaiter().GetResult())), TimeSpan.FromSeconds(1));
        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory);
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, nested.Count);
        Assert.All(nested, r => Assert.Equal(KeyRingFailureKind.InvalidScenario, r.FailureKind));
        Assert.Equal(0, nestedCalls);
    }
}
