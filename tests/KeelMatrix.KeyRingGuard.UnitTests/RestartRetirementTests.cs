using Microsoft.AspNetCore.DataProtection;

namespace KeelMatrix.KeyRingGuard.UnitTests;

[Collection("Provider lifecycle")]
public sealed class RestartRetirementTests
{
    [Theory]
    [InlineData(31, false)]
    [InlineData(32, false)]
    [InlineData(33, false)]
    [InlineData(31, true)]
    [InlineData(32, true)]
    [InlineData(33, true)]
    public async Task RetirementRetainsBoundedCapacityUntilTeardownFinishes(int count, bool asynchronous)
    {
        using var release = new ManualResetEventSlim();
        var created = 0;
        var entered = 0;
        var disposed = 0;
        var factory = BufferOwnershipTests.Factory(asynchronous, () =>
        {
            Interlocked.Increment(ref created);
            return new FakeProvider(dispose: () =>
            {
                Interlocked.Increment(ref entered);
                release.Wait();
                Interlocked.Increment(ref disposed);
            });
        }, TimeSpan.FromMilliseconds(500));
        var tasks = Enumerable.Range(0, count).Select(_ => KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory)).ToArray();
        try
        {
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref entered) == Math.Min(count, 32), TimeSpan.FromSeconds(5)));
            var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(4));
            Assert.All(results, result => Assert.Equal(KeyRingFailureKind.Timeout, result.FailureKind));
            Assert.Equal(Math.Min(count, 32), created); // No B, even after the observer stopped.
            if (count >= 32)
            {
                using var caller = new CancellationTokenSource();
                var queued = KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory, cancellationToken: caller.Token);
                caller.Cancel();
                Assert.Equal(KeyRingFailureKind.Canceled, (await queued.WaitAsync(TimeSpan.FromSeconds(2))).FailureKind);
                Assert.Equal(32, created);
            }
        }
        finally
        {
            release.Set();
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(4));
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref disposed) == Volatile.Read(ref created), TimeSpan.FromSeconds(3)));
        }
        var recovered = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity,
            BufferOwnershipTests.Factory(asynchronous, () => new FakeProvider()));
        Assert.True(recovered.Succeeded, recovered.Message);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RestartClosesExclusiveOwnershipBeforeReopening(bool asynchronous, bool losesStore)
    {
        var active = 0;
        var calls = 0;
        var disposed = 0;
        var readable = true;
        var sequence = new List<string>();
        var factory = BufferOwnershipTests.Factory(asynchronous, () =>
        {
            Assert.Equal(0, active);
            active++;
            var call = ++calls;
            sequence.Add($"create-{call}");
            return new FakeProvider(protect: input => { sequence.Add("protect"); return input; },
                unprotect: input => { sequence.Add("read"); return readable ? input : []; },
                dispose: () =>
                {
                    sequence.Add($"dispose-{call}");
                    active--;
                    disposed++;
                    if (losesStore) { readable = false; }
                });
        });
        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory);
        Assert.Equal(losesStore ? KeyRingFailureKind.Unprotect : KeyRingFailureKind.None, result.FailureKind);
        Assert.Equal(2, disposed);
        Assert.Equal(new[] { "create-1", "protect", "dispose-1", "create-2", "read", "dispose-2" }, sequence);
    }

    [Theory]
    [InlineData(false, "throw")]
    [InlineData(false, "timeout")]
    [InlineData(false, "cancel")]
    [InlineData(true, "throw")]
    [InlineData(true, "timeout")]
    [InlineData(true, "cancel")]
    public async Task FailedOrStoppedRetirementNeverCreatesB(bool asynchronous, string outcome)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var caller = new CancellationTokenSource();
        var calls = 0;
        var attempts = 0;
        var factory = BufferOwnershipTests.Factory(asynchronous, () =>
        {
            Interlocked.Increment(ref calls);
            return new FakeProvider(dispose: () =>
            {
                Interlocked.Increment(ref attempts);
                entered.Set();
                if (outcome == "throw") { throw new InvalidOperationException("private-retirement"); }
                release.Wait();
            });
        }, TimeSpan.FromMilliseconds(150));
        var task = KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory, cancellationToken: caller.Token);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            if (outcome == "cancel") { caller.Cancel(); }
            var result = await task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(outcome switch
            {
                "throw" => KeyRingFailureKind.ProviderCreation,
                "cancel" => KeyRingFailureKind.Canceled,
                _ => KeyRingFailureKind.Timeout,
            }, result.FailureKind);
            Assert.Equal(1, calls);
            Assert.Equal(1, attempts);
            Assert.DoesNotContain("private-retirement", result.Message, StringComparison.Ordinal);
        }
        finally { release.Set(); await task.WaitAsync(TimeSpan.FromSeconds(3)); }
        await Task.Delay(30);
        Assert.Equal(1, attempts);
        var recovered = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity,
            BufferOwnershipTests.Factory(asynchronous, () => new FakeProvider()));
        Assert.True(recovered.Succeeded, recovered.Message);
    }
}
