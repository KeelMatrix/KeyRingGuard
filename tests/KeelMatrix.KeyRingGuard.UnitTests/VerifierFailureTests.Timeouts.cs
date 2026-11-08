using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;

namespace KeelMatrix.KeyRingGuard.UnitTests;

public sealed partial class VerifierFailureTests
{
    [Fact]
    public async Task TimeoutIsBoundedWhenFactoryIgnoresCancellation()
    {
        var completion = new TaskCompletionSource<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new KeyRingProviderFactory(
            _ => completion.Task,
            TimeSpan.FromMilliseconds(50));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.Timeout, result.FailureKind);
        var disposed = 0;
        completion.SetResult(new FakeProvider(dispose: () => Interlocked.Increment(ref disposed)));
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref disposed) == 1, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task LateProviderCompletionIsDisposedAfterATimeout()
    {
        var completion = new TaskCompletionSource<IDataProtectionProvider>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = 0;
        var provider = new FakeProvider(dispose: () => Interlocked.Increment(ref disposed));
        var factory = new KeyRingProviderFactory(
            _ => completion.Task,
            TimeSpan.FromMilliseconds(40));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory);

        Assert.Equal(KeyRingFailureKind.Timeout, result.FailureKind);
        completion.SetResult(provider);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref disposed) == 1, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task LateSynchronousProviderCompletionIsDisposedAfterATimeout()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = 0;
        var provider = new FakeProvider(dispose: () => Interlocked.Increment(ref disposed));
        var factory = new KeyRingProviderFactory(
            _ =>
            {
                completion.Task.GetAwaiter().GetResult();
                return provider;
            },
            TimeSpan.FromMilliseconds(40));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory);

        Assert.Equal(KeyRingFailureKind.Timeout, result.FailureKind);
        completion.SetResult(true);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref disposed) == 1, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task CancellationIsReportedSeparately()
    {
        using var cancellation = new CancellationTokenSource();
        var factory = new KeyRingProviderFactory(
            async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new FakeProvider();
            },
            TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory, cancellationToken: cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.Canceled, result.FailureKind);
    }

    [Fact]
    public async Task SynchronousProviderFactoryTimeoutIsBounded()
    {
        var factory = new KeyRingProviderFactory(
            _ =>
            {
                Thread.Sleep(250);
                return new FakeProvider();
            },
            TimeSpan.FromMilliseconds(40));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.Timeout, result.FailureKind);
        Assert.InRange(result.Duration, TimeSpan.Zero, TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task AsyncProviderFactoryInvocationTimeoutIsBoundedBeforeItReturnsATask()
    {
        var factory = new KeyRingProviderFactory(
            (Func<CancellationToken, Task<IDataProtectionProvider>>)(_ =>
            {
                Thread.Sleep(250);
                return Task.FromResult<IDataProtectionProvider>(new FakeProvider());
            }),
            TimeSpan.FromMilliseconds(40));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.Timeout, result.FailureKind);
        Assert.InRange(result.Duration, TimeSpan.Zero, TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task SynchronousProtectTimeoutIsBounded()
    {
        var factory = new KeyRingProviderFactory(
            _ => new FakeProvider(protect: value =>
            {
                Thread.Sleep(250);
                return value.ToArray();
            }),
            TimeSpan.FromMilliseconds(40));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.Timeout, result.FailureKind);
    }

    [Fact]
    public async Task TimedOutProtectDoesNotRaceProviderDisposal()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var disposed = 0;
        var disposedBeforeLateProtect = 0;
        var provider = new FakeProvider(
            protect: value =>
            {
                started.Set();
                release.Wait();
                if (Volatile.Read(ref disposed) != 0)
                {
                    Interlocked.Increment(ref disposedBeforeLateProtect);
                }

                return value.ToArray();
            },
            dispose: () => Interlocked.Increment(ref disposed));
        var factory = new KeyRingProviderFactory(_ => provider, TimeSpan.FromMilliseconds(40));

        try
        {
            var verification = KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory);
            Assert.True(started.Wait(TimeSpan.FromSeconds(1)));

            var result = await verification;

            Assert.Equal(KeyRingFailureKind.Timeout, result.FailureKind);
            Assert.Equal(0, Volatile.Read(ref disposed));

            release.Set();
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref disposed) == 1, TimeSpan.FromSeconds(1)));
            Assert.Equal(0, Volatile.Read(ref disposedBeforeLateProtect));
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task SynchronousUnprotectTimeoutIsBounded()
    {
        var factoryCalls = 0;
        var factory = new KeyRingProviderFactory(
            _ =>
            {
                if (Interlocked.Increment(ref factoryCalls) == 2)
                {
                    return new FakeProvider(unprotect: value =>
                    {
                        Thread.Sleep(250);
                        return value.ToArray();
                    });
                }

                return new FakeProvider();
            },
            TimeSpan.FromMilliseconds(40));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.Timeout, result.FailureKind);
    }

    [Fact]
    public async Task SynchronousKeyManagerCreationTimeoutIsBounded()
    {
        var factory = new KeyRingProviderFactory(
            (Func<CancellationToken, IDataProtectionProvider>)(_ => new FakeProvider()),
            TimeSpan.FromMilliseconds(40),
            (Func<CancellationToken, IKeyManager>)(_ =>
            {
                Thread.Sleep(250);
                return new SlowKeyManager();
            }),
            static (manager, activationDate, expirationDate, cancellationToken) =>
                Task.FromResult(manager.CreateNewKey(activationDate, expirationDate)));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.Timeout, result.FailureKind);
    }

    [Fact]
    public async Task SynchronousRotationTimeoutIsBounded()
    {
        var factory = new KeyRingProviderFactory(
            (Func<CancellationToken, IDataProtectionProvider>)(_ => new FakeProvider()),
            TimeSpan.FromMilliseconds(40),
            (Func<CancellationToken, IKeyManager>)(_ => new SlowKeyManager()),
            static (manager, activationDate, expirationDate, cancellationToken) =>
                Task.FromResult(manager.CreateNewKey(activationDate, expirationDate)));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.Timeout, result.FailureKind);
    }

    [Fact]
    public async Task RotationCallbackInvocationTimeoutIsBoundedBeforeItReturnsATask()
    {
        var factory = new KeyRingProviderFactory(
            (Func<CancellationToken, IDataProtectionProvider>)(_ => new FakeProvider()),
            TimeSpan.FromMilliseconds(40),
            (Func<CancellationToken, IKeyManager>)(_ => new NullKeyManager()),
            static (manager, activationDate, expirationDate, cancellationToken) =>
            {
                Thread.Sleep(250);
                return Task.FromResult<IKey>(new TestKey(Guid.NewGuid(), activationDate, expirationDate));
            });

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.Timeout, result.FailureKind);
        Assert.InRange(result.Duration, TimeSpan.Zero, TimeSpan.FromMilliseconds(200));
    }
}
