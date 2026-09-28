using System.Collections.Concurrent;
using System.Security.Cryptography;
using KeelMatrix.KeyRingGuard;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;

namespace KeelMatrix.KeyRingGuard.UnitTests;

public sealed class VerifierFailureTests
{
    [Fact]
    public async Task ProviderFailureIsCategorizedWithoutExceptionText()
    {
        const string secret = "<key secret> provider-path=do-not-emit";
        var factory = new KeyRingProviderFactory(
            (Func<CancellationToken, Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>)(_ => throw new InvalidOperationException(secret)),
            TimeSpan.FromSeconds(1));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.ProviderCreation, result.FailureKind);
        Assert.DoesNotContain(secret, result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("key", result.Message, StringComparison.OrdinalIgnoreCase);
    }

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
    public async Task RestartFactoryMustCreateAnIndependentProvider()
    {
        var provider = new FakeProvider();
        var factory = new KeyRingProviderFactory(_ => provider, TimeSpan.FromSeconds(1));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.ProviderCreation, result.FailureKind);
    }

    [Fact]
    public async Task ReplicaFactoriesMustCreateIndependentProviders()
    {
        var provider = new FakeProvider();
        var firstFactory = new KeyRingProviderFactory(_ => provider, TimeSpan.FromSeconds(1));
        var secondFactory = new KeyRingProviderFactory(_ => provider, TimeSpan.FromSeconds(1));

        var result = await KeyRingVerifier.VerifyAsync(
            KeyRingScenario.ReplicaSharing,
            firstFactory,
            secondFactory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.ProviderCreation, result.FailureKind);
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
    public async Task UnexpectedSuccessfulCrossUnprotectFailsClosed()
    {
        var factory = new KeyRingProviderFactory(_ => new FakeProvider(), TimeSpan.FromSeconds(1));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.PurposeIsolation, factory, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.UnexpectedCrossUnprotect, result.FailureKind);
    }

    [Fact]
    public async Task ApplicationIsolationProviderFailureDoesNotPass()
    {
        var boundary = new KeyRingBoundary();
        var brokenFactory = new KeyRingProviderFactory(
            _ => new FakeProvider(unprotect: _ => throw new InvalidOperationException("unrelated provider failure")),
            TimeSpan.FromSeconds(1),
            null,
            boundary);
        var controlFactory = new KeyRingProviderFactory(_ => new FakeProvider(), TimeSpan.FromSeconds(1), null, boundary);

        var result = await KeyRingVerifier.VerifyWithIsolationControlAsync(
            KeyRingScenario.ApplicationIsolation,
            brokenFactory,
            brokenFactory,
            new KeyRingIsolationControl(controlFactory, controlFactory),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.Unprotect, result.FailureKind);
    }

    [Fact]
    public async Task PurposeIsolationProviderFailureDoesNotPass()
    {
        var factory = new KeyRingProviderFactory(
            _ => new FakeProvider(unprotect: _ => throw new InvalidOperationException("unrelated provider failure")),
            TimeSpan.FromSeconds(1));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.PurposeIsolation, factory, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.Unprotect, result.FailureKind);
    }

    [Fact]
    public async Task CryptographicExceptionInSameBoundaryControlDoesNotPassAsIsolation()
    {
        var factory = new KeyRingProviderFactory(
            _ => new FakeProvider(unprotect: _ => throw new CryptographicException("rejection")),
            TimeSpan.FromSeconds(1));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.PurposeIsolation, factory, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.Unprotect, result.FailureKind);
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
    public async Task AsyncProviderFailureIsCategorizedWithoutExceptionText()
    {
        const string secret = "<async key secret> provider-path=do-not-emit";
        var factory = new KeyRingProviderFactory(
            (Func<CancellationToken, Task<IDataProtectionProvider>>)(_ =>
                Task.FromException<IDataProtectionProvider>(new InvalidOperationException(secret))),
            TimeSpan.FromSeconds(1));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.ProviderCreation, result.FailureKind);
        Assert.DoesNotContain(secret, result.Message, StringComparison.Ordinal);
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
    public async Task CanceledQueuedSynchronousFactoryDoesNotRunAfterCapacityIsReleased()
    {
        using var release = new ManualResetEventSlim();
        var started = 0;
        var lateInvocations = 0;
        var blockingFactories = Enumerable.Range(0, 32)
            .Select(_ => new KeyRingProviderFactory(
                _ =>
                {
                    Interlocked.Increment(ref started);
                    release.Wait(CancellationToken.None);
                    return new FakeProvider();
                },
                TimeSpan.FromSeconds(5)))
            .ToArray();
        var running = blockingFactories
            .Select(factory => KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory))
            .ToArray();
        var queuedFactory = new KeyRingProviderFactory(
            _ =>
            {
                Interlocked.Increment(ref lateInvocations);
                return new FakeProvider();
            },
            TimeSpan.FromMilliseconds(40));

        try
        {
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref started) == 32, TimeSpan.FromSeconds(5)));
            var queuedResult = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, queuedFactory);

            Assert.Equal(KeyRingFailureKind.Timeout, queuedResult.FailureKind);
            release.Set();
            await Task.WhenAll(running);
            Assert.Equal(0, Volatile.Read(ref lateInvocations));
        }
        finally
        {
            release.Set();
            await Task.WhenAll(running);
        }
    }

    [Fact]
    public async Task QueuedSynchronousProtectTimeoutIsReportedAsTimeout()
    {
        using var release = new ManualResetEventSlim();
        using var capacityObserved = new ManualResetEventSlim();
        var started = 0;
        Task[] blockers = [];
        var blockerFactory = new KeyRingProviderFactory(
            _ =>
            {
                Interlocked.Increment(ref started);
                release.Wait(CancellationToken.None);
                return new FakeProvider();
            },
            TimeSpan.FromSeconds(5));
        var provider = new FakeProvider(
            createProtector: _ =>
            {
                using (ExecutionContext.SuppressFlow())
                {
                    blockers = Enumerable.Range(0, 64)
                        .Select(_ => Task.Run(() => KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, blockerFactory)))
                        .ToArray();
                }

                if (SpinWait.SpinUntil(() => Volatile.Read(ref started) >= 30, TimeSpan.FromSeconds(5)))
                {
                    capacityObserved.Set();
                }

                return new FakeProtector(null, payload => payload);
            });
        var targetFactory = new KeyRingProviderFactory(_ => provider, TimeSpan.FromMilliseconds(500));

        try
        {
            var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, targetFactory);

            Assert.Equal(KeyRingFailureKind.Timeout, result.FailureKind);
            Assert.True(
                capacityObserved.Wait(TimeSpan.FromSeconds(2)),
                $"started={Volatile.Read(ref started)}, blockers={blockers.Length}");
            Assert.InRange(Volatile.Read(ref started), 30, 32);
        }
        finally
        {
            release.Set();
            await Task.WhenAll(blockers);
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
                Task.Run(() => manager.CreateNewKey(activationDate, expirationDate), cancellationToken));

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

    [Fact]
    public async Task MissingSecondaryFactoryIsAnInvalidScenario()
    {
        var factory = new KeyRingProviderFactory(_ => new FakeProvider(), TimeSpan.FromSeconds(1));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.ReplicaSharing, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.InvalidScenario, result.FailureKind);
    }

    [Fact]
    public async Task RotationRequiresExplicitKeyManagerFactory()
    {
        var factory = new KeyRingProviderFactory(_ => new FakeProvider(), TimeSpan.FromSeconds(1));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.RotationUnavailable, result.FailureKind);
    }

    [Fact]
    public async Task RotationRequiresAnObservedNewActiveKey()
    {
        var factory = new KeyRingProviderFactory(
            (Func<CancellationToken, IDataProtectionProvider>)(_ => new FakeProvider()),
            TimeSpan.FromSeconds(1),
            (Func<CancellationToken, IKeyManager>)(_ => new NullKeyManager()),
            static (manager, activationDate, expirationDate, cancellationToken) =>
                Task.FromResult(manager.CreateNewKey(activationDate, expirationDate)));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.RotationFailure, result.FailureKind);
    }

    [Fact]
    public async Task RotationRejectsCallbackReturningPreExistingKey()
    {
        var manager = new ExistingKeyManager();
        var factory = new KeyRingProviderFactory(
            (Func<CancellationToken, IDataProtectionProvider>)(_ => new FakeProvider()),
            TimeSpan.FromSeconds(1),
            _ => manager,
            static (keyManager, activationDate, expirationDate, cancellationToken) =>
                Task.FromResult(keyManager.CreateNewKey(activationDate, expirationDate)));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.RotationFailure, result.FailureKind);
    }

    [Fact]
    public async Task AsyncRotationRejectsCallbackReturningPreExistingKey()
    {
        var manager = new ExistingKeyManager();
        var factory = new KeyRingProviderFactory(
            (Func<CancellationToken, Task<IDataProtectionProvider>>)(_ =>
                Task.FromResult<IDataProtectionProvider>(new FakeProvider())),
            TimeSpan.FromSeconds(1),
            (Func<CancellationToken, Task<IKeyManager>>)(_ => Task.FromResult<IKeyManager>(manager)),
            async (keyManager, activationDate, expirationDate, cancellationToken) =>
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                return keyManager.CreateNewKey(activationDate, expirationDate);
            });

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.RotationFailure, result.FailureKind);
    }

    [Fact]
    public async Task RotationSnapshotsAndInvokesCallbackWithinOneScheduledOperation()
    {
        var manager = new ThreadScopedRotationKeyManager();
        var factory = new KeyRingProviderFactory(
            (Func<CancellationToken, IDataProtectionProvider>)(_ => new FakeProvider(
                protect: payload => manager.CreatedKey is { } key
                    ? CreateProtectedPayload(payload, key.KeyId)
                    : payload.ToArray(),
                unprotect: UnprotectPayload)),
            TimeSpan.FromSeconds(1),
            _ => manager,
            static (keyManager, activationDate, expirationDate, _) =>
            {
                Assert.True(RotationThreadProbe.SnapshotSeenOnCurrentThread);
                return Task.FromResult(keyManager.CreateNewKey(activationDate, expirationDate));
            });

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory);

        Assert.True(result.Succeeded, result.Message);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    public async Task ConcurrentRotationsCompleteBelowAtAndAboveSchedulerCapacity(int concurrentVerifications)
    {
        var factories = Enumerable.Range(0, concurrentVerifications)
            .Select(_ => CreateFastRotationFactory())
            .ToArray();

        var results = await Task.WhenAll(
                factories.Select(factory => KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory)))
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(results, result => Assert.True(result.Succeeded, result.Message));
    }

    [Theory]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    public async Task NestedVerificationFromRotationCallbacksFailsClosedWithoutSchedulerDeadlock(int concurrentVerifications)
    {
        var nestedResults = new ConcurrentBag<KeyRingVerificationResult>();
        var nestedFactoryInvocations = 0;
        var factories = Enumerable.Range(0, concurrentVerifications)
            .Select(_ => CreateReentrantRotationFactory(nestedResults, () => Interlocked.Increment(ref nestedFactoryInvocations)))
            .ToArray();

        var results = await Task.WhenAll(
                factories.Select(factory => KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory)))
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(results, result => Assert.True(result.Succeeded, result.Message));
        Assert.Equal(concurrentVerifications, nestedResults.Count);
        Assert.All(
            nestedResults,
            result =>
            {
                Assert.False(result.Succeeded);
                Assert.Equal(KeyRingFailureKind.InvalidScenario, result.FailureKind);
                Assert.Equal(
                    "Verification cannot be started from a provider, key-manager, or rotation callback.",
                    result.Message);
            });
        Assert.Equal(0, Volatile.Read(ref nestedFactoryInvocations));

        var followUpResults = await Task.WhenAll(
                Enumerable.Range(0, 33)
                    .Select(_ => KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, CreateFastRotationFactory())))
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(followUpResults, result => Assert.True(result.Succeeded, result.Message));
    }

    [Theory]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    public async Task SuppressedContextNestedVerificationCompletesWithinBoundAndLeavesSchedulerUsable(int concurrentVerifications)
    {
        var factories = Enumerable.Range(0, concurrentVerifications)
            .Select(_ => CreateSuppressedContextReentrantRotationFactory())
            .ToArray();

        var results = await Task.WhenAll(
                factories.Select(factory => KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory)))
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(results.Length == concurrentVerifications);

        var followUpResults = await Task.WhenAll(
                Enumerable.Range(0, 33)
                    .Select(_ => KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, CreateFastRotationFactory())))
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(followUpResults, result => Assert.True(result.Succeeded, result.Message));
    }

    [Theory]
    [InlineData("provider-sync", 31)]
    [InlineData("provider-sync", 32)]
    [InlineData("provider-sync", 33)]
    [InlineData("provider-async", 31)]
    [InlineData("provider-async", 32)]
    [InlineData("provider-async", 33)]
    [InlineData("create-protector", 31)]
    [InlineData("create-protector", 32)]
    [InlineData("create-protector", 33)]
    [InlineData("protect", 31)]
    [InlineData("protect", 32)]
    [InlineData("protect", 33)]
    [InlineData("unprotect", 31)]
    [InlineData("unprotect", 32)]
    [InlineData("unprotect", 33)]
    [InlineData("key-manager-sync", 31)]
    [InlineData("key-manager-sync", 32)]
    [InlineData("key-manager-sync", 33)]
    [InlineData("key-manager-async", 31)]
    [InlineData("key-manager-async", 32)]
    [InlineData("key-manager-async", 33)]
    [InlineData("rotation-sync", 31)]
    [InlineData("rotation-sync", 32)]
    [InlineData("rotation-sync", 33)]
    [InlineData("rotation-async", 31)]
    [InlineData("rotation-async", 32)]
    [InlineData("rotation-async", 33)]
    public async Task NestedVerificationFromEveryCallbackIsRejectedBeforeAdmission(string callbackKind, int concurrentVerifications)
    {
        var probes = Enumerable.Range(0, concurrentVerifications).Select(_ => new ReentryProbe()).ToArray();
        var factories = probes.Select(probe => CreateReentryFactory(callbackKind, probe)).ToArray();
        var scenario = callbackKind is "key-manager-sync" or "key-manager-async" or "rotation-sync" or "rotation-async"
            ? KeyRingScenario.RotationContinuity
            : KeyRingScenario.RestartContinuity;

        var results = await Task.WhenAll(
                factories.Select(factory => KeyRingVerifier.VerifyAsync(scenario, factory)))
            .WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(results.Length == concurrentVerifications);
        Assert.All(results, result => Assert.True(result.Succeeded, result.Message));
        Assert.All(probes, probe =>
        {
            var nestedResult = Assert.Single(probe.NestedResults);
            Assert.False(nestedResult.Succeeded);
            Assert.Equal(KeyRingFailureKind.InvalidScenario, nestedResult.FailureKind);
            Assert.Equal(
                "Verification cannot be started from a provider, key-manager, or rotation callback.",
                nestedResult.Message);
            Assert.Equal(0, Volatile.Read(ref probe.NestedFactoryInvocations));
        });
    }

    [Fact]
    public async Task FaultedCanceledAndTimedOutNestedCallsAreRejectedBeforeAdmission()
    {
        var probe = new NestedOutcomeProbe();
        var manager = new ThreadScopedRotationKeyManager();
        var factory = new KeyRingProviderFactory(
            _ => new FakeProvider(
                protect: payload => manager.CreatedKey is { } key
                    ? CreateProtectedPayload(payload, key.KeyId)
                    : payload.ToArray(),
                unprotect: UnprotectPayload),
            TimeSpan.FromSeconds(2),
            _ => manager,
            async (keyManager, activationDate, expirationDate, cancellationToken) =>
            {
                await probe.RunAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return keyManager.CreateNewKey(activationDate, expirationDate);
            });

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(3, probe.Results.Count);
        Assert.All(probe.Results, nestedResult =>
        {
            Assert.False(nestedResult.Succeeded);
            Assert.Equal(KeyRingFailureKind.InvalidScenario, nestedResult.FailureKind);
            Assert.Equal(
                "Verification cannot be started from a provider, key-manager, or rotation callback.",
                nestedResult.Message);
        });
        Assert.Equal(0, Volatile.Read(ref probe.NestedFactoryInvocations));
    }

    [Fact]
    public async Task AFailedRotationDoesNotStarveOtherRotationsAtCapacity()
    {
        var factories = Enumerable.Range(0, 32)
            .Select(index => CreateFastRotationFactory(index == 0))
            .ToArray();

        var results = await Task.WhenAll(
                factories.Select(factory => KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory)))
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(KeyRingFailureKind.RotationFailure, results[0].FailureKind);
        Assert.Equal(31, results.Count(result => result.Succeeded));
    }

    [Fact]
    public async Task AsyncRotationCallbackHonorsCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var callbackStarted = new ManualResetEventSlim();
        var factory = new KeyRingProviderFactory(
            (Func<CancellationToken, Task<IDataProtectionProvider>>)(_ =>
                Task.FromResult<IDataProtectionProvider>(new FakeProvider())),
            TimeSpan.FromSeconds(5),
            (Func<CancellationToken, Task<IKeyManager>>)(_ => Task.FromResult<IKeyManager>(new NullKeyManager())),
            async (manager, activationDate, expirationDate, cancellationToken) =>
            {
                callbackStarted.Set();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return manager.CreateNewKey(activationDate, expirationDate);
            });

        var verification = KeyRingVerifier.VerifyAsync(
            KeyRingScenario.RotationContinuity,
            factory,
            cancellationToken: cancellation.Token);
        Assert.True(callbackStarted.Wait(TimeSpan.FromSeconds(1)));

        cancellation.Cancel();
        var result = await verification;

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.Canceled, result.FailureKind);
    }

    private static KeyRingProviderFactory CreateFastRotationFactory(bool failRotation = false)
    {
        var manager = new ThreadScopedRotationKeyManager();
        return new KeyRingProviderFactory(
            (Func<CancellationToken, IDataProtectionProvider>)(_ => new FakeProvider(
                protect: payload => manager.CreatedKey is { } key
                    ? CreateProtectedPayload(payload, key.KeyId)
                    : payload.ToArray(),
                unprotect: UnprotectPayload)),
            TimeSpan.FromSeconds(2),
            (Func<CancellationToken, IKeyManager>)(_ => manager),
            (keyManager, activationDate, expirationDate, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (failRotation)
                {
                    throw new InvalidOperationException("rotation callback failure");
                }

                return Task.FromResult(keyManager.CreateNewKey(activationDate, expirationDate));
            });
    }

    private static KeyRingProviderFactory CreateReentrantRotationFactory(
        ConcurrentBag<KeyRingVerificationResult> nestedResults,
        Action nestedFactoryStarted)
    {
        var manager = new ThreadScopedRotationKeyManager();
        return new KeyRingProviderFactory(
            _ => new FakeProvider(
                protect: payload => manager.CreatedKey is { } key
                    ? CreateProtectedPayload(payload, key.KeyId)
                    : payload.ToArray(),
                unprotect: UnprotectPayload),
            TimeSpan.FromSeconds(2),
            _ => manager,
            async (keyManager, activationDate, expirationDate, cancellationToken) =>
            {
                var nestedResult = await KeyRingVerifier.VerifyAsync(
                        KeyRingScenario.RestartContinuity,
                        new KeyRingProviderFactory(
                            _ =>
                            {
                                nestedFactoryStarted();
                                return new FakeProvider();
                            },
                            TimeSpan.FromSeconds(1)),
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                nestedResults.Add(nestedResult);
                cancellationToken.ThrowIfCancellationRequested();
                return keyManager.CreateNewKey(activationDate, expirationDate);
            });
    }

    private static KeyRingProviderFactory CreateSuppressedContextReentrantRotationFactory()
    {
        var manager = new ThreadScopedRotationKeyManager();
        return new KeyRingProviderFactory(
            _ => new FakeProvider(
                protect: payload => manager.CreatedKey is { } key
                    ? CreateProtectedPayload(payload, key.KeyId)
                    : payload.ToArray(),
                unprotect: UnprotectPayload),
            TimeSpan.FromSeconds(2),
            _ => manager,
            async (keyManager, activationDate, expirationDate, cancellationToken) =>
            {
                Task<KeyRingVerificationResult> nestedTask;
                using (ExecutionContext.SuppressFlow())
                {
                    nestedTask = Task.Run(
                        () => KeyRingVerifier.VerifyAsync(
                            KeyRingScenario.RestartContinuity,
                            new KeyRingProviderFactory(_ => new FakeProvider(), TimeSpan.FromMilliseconds(100)),
                            cancellationToken: cancellationToken));
                }

                await nestedTask.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return keyManager.CreateNewKey(activationDate, expirationDate);
            });
    }

    private static KeyRingProviderFactory CreateReentryFactory(string callbackKind, ReentryProbe probe)
    {
        var manager = new ThreadScopedRotationKeyManager();
        var isRotation = callbackKind is "key-manager-sync" or "key-manager-async" or "rotation-sync" or "rotation-async";

        IDataProtectionProvider CreateProvider()
        {
            Func<byte[], byte[]> protect = payload =>
            {
                if (callbackKind == "protect")
                {
                    probe.TriggerSync(CancellationToken.None);
                }

                return isRotation && manager.CreatedKey is { } key
                    ? CreateProtectedPayload(payload, key.KeyId)
                    : payload.ToArray();
            };
            Func<byte[], byte[]> unprotect = payload =>
            {
                if (callbackKind == "unprotect")
                {
                    probe.TriggerSync(CancellationToken.None);
                }

                return UnprotectPayload(payload);
            };

            return new FakeProvider(
                protect: protect,
                unprotect: unprotect,
                createProtector: _ =>
                {
                    if (callbackKind == "create-protector")
                    {
                        probe.TriggerSync(CancellationToken.None);
                    }

                    return new FakeProtector(protect, unprotect);
                });
        }

        if (callbackKind == "provider-sync")
        {
            return new KeyRingProviderFactory(
                token =>
                {
                    probe.TriggerSync(token);
                    return CreateProvider();
                },
                TimeSpan.FromSeconds(2));
        }

        if (callbackKind == "provider-async")
        {
            return new KeyRingProviderFactory(
                (Func<CancellationToken, Task<IDataProtectionProvider>>)(async token =>
                {
                    await probe.TriggerAsync(token).ConfigureAwait(false);
                    return CreateProvider();
                }),
                TimeSpan.FromSeconds(2));
        }

        if (!isRotation)
        {
            return new KeyRingProviderFactory(_ => CreateProvider(), TimeSpan.FromSeconds(2));
        }

        if (callbackKind == "key-manager-sync" || callbackKind == "rotation-sync")
        {
            return new KeyRingProviderFactory(
                _ => CreateProvider(),
                TimeSpan.FromSeconds(2),
                token =>
                {
                    if (callbackKind == "key-manager-sync")
                    {
                        probe.TriggerSync(token);
                    }

                    return manager;
                },
                (keyManager, activationDate, expirationDate, token) =>
                {
                    if (callbackKind == "rotation-sync")
                    {
                        probe.TriggerSync(token);
                    }

                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(keyManager.CreateNewKey(activationDate, expirationDate));
                });
        }

        return new KeyRingProviderFactory(
            (Func<CancellationToken, Task<IDataProtectionProvider>>)(_ => Task.FromResult(CreateProvider())),
            TimeSpan.FromSeconds(2),
            (Func<CancellationToken, Task<IKeyManager>>)(async token =>
            {
                if (callbackKind == "key-manager-async")
                {
                    await probe.TriggerAsync(token).ConfigureAwait(false);
                }

                return manager;
            }),
            async (keyManager, activationDate, expirationDate, token) =>
            {
                if (callbackKind == "rotation-async")
                {
                    await probe.TriggerAsync(token).ConfigureAwait(false);
                }

                token.ThrowIfCancellationRequested();
                return keyManager.CreateNewKey(activationDate, expirationDate);
            });
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("expired")]
    [InlineData("not-yet-active")]
    [InlineData("revoked")]
    [InlineData("mismatched")]
    public async Task RotationRejectsUnsafeObservedKeyMetadata(string caseName)
    {
        var manager = new MetadataKeyManager(caseName);
        var factory = new KeyRingProviderFactory(
            (Func<CancellationToken, IDataProtectionProvider>)(_ => new FakeProvider()),
            TimeSpan.FromSeconds(1),
            _ => manager,
            static (keyManager, activationDate, expirationDate, cancellationToken) =>
                Task.FromResult(keyManager.CreateNewKey(activationDate, expirationDate)));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.RotationFailure, result.FailureKind);
    }

    [Fact]
    public async Task RotationObservationExceptionsAreSanitizedAndBounded()
    {
        var factory = new KeyRingProviderFactory(
            (Func<CancellationToken, IDataProtectionProvider>)(_ => new FakeProvider()),
            TimeSpan.FromSeconds(1),
            (Func<CancellationToken, IKeyManager>)(_ => new ThrowingObservationKeyManager()),
            static (manager, activationDate, expirationDate, cancellationToken) =>
                Task.FromResult(manager.CreateNewKey(activationDate, expirationDate)));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.RotationFailure, result.FailureKind);
    }

    [Fact]
    public async Task RotationAcceptsStandardPayloadWithBigEndianKeyId()
    {
        var manager = new ThreadScopedRotationKeyManager();
        var factory = new KeyRingProviderFactory(
            _ => new FakeProvider(
                protect: payload => manager.CreatedKey is { } key
                    ? CreateProtectedPayload(payload, key.KeyId, bigEndian: true)
                    : payload.ToArray(),
                unprotect: UnprotectPayload),
            TimeSpan.FromSeconds(1),
            _ => manager,
            static (keyManager, activationDate, expirationDate, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(keyManager.CreateNewKey(activationDate, expirationDate));
            });

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory);

        Assert.True(result.Succeeded, result.Message);
    }

    [Fact]
    public async Task RotationReportsStandardPayloadWithoutAdoptionAsRotationFailure()
    {
        var manager = new ThreadScopedRotationKeyManager();
        var factory = new KeyRingProviderFactory(
            _ => new FakeProvider(
                protect: payload => manager.CreatedKey is { } ? CreateProtectedPayload(payload, Guid.NewGuid()) : payload.ToArray(),
                unprotect: UnprotectPayload),
            TimeSpan.FromSeconds(1),
            _ => manager,
            static (keyManager, activationDate, expirationDate, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(keyManager.CreateNewKey(activationDate, expirationDate));
            });

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.RotationFailure, result.FailureKind);
        Assert.Contains("did not adopt", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("opaque")]
    [InlineData("short")]
    [InlineData("malformed")]
    public async Task RotationReportsUnsupportedPayloadFormatAsUnavailable(string payloadKind)
    {
        var manager = new ThreadScopedRotationKeyManager();
        var factory = new KeyRingProviderFactory(
            _ => new FakeProvider(
                protect: payload => manager.CreatedKey is { } key
                    ? payloadKind switch
                    {
                        "opaque" => CreateOpaqueProtectedPayload(payload),
                        "short" => [0x09, 0xF0, 0xC9, 0xF0],
                        "malformed" => [0x08, 0xF0, 0xC9, 0xF0, .. new byte[16], .. payload],
                        _ => throw new ArgumentOutOfRangeException(nameof(payloadKind)),
                    }
                    : payload.ToArray(),
                unprotect: UnprotectOpaquePayload),
            TimeSpan.FromSeconds(1),
            _ => manager,
            static (keyManager, activationDate, expirationDate, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(keyManager.CreateNewKey(activationDate, expirationDate));
            });

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.RotationUnavailable, result.FailureKind);
        Assert.Contains("standard ASP.NET Core Data Protection payload format", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryCreatedProviderIsDisposedAcrossScenarioAndEarlyExitPaths()
    {
        var disposed = 0;
        KeyRingProviderFactory Factory() => new(_ => new FakeProvider(dispose: () => Interlocked.Increment(ref disposed)), TimeSpan.FromSeconds(1));

        await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, Factory());
        await KeyRingVerifier.VerifyAsync(KeyRingScenario.ReplicaSharing, Factory(), Factory());
        await KeyRingVerifier.VerifyAsync(KeyRingScenario.PurposeIsolation, Factory());

        var boundary = new KeyRingBoundary();
        await KeyRingVerifier.VerifyWithIsolationControlAsync(
            KeyRingScenario.ApplicationIsolation,
            new KeyRingProviderFactory(_ => new FakeProvider(dispose: () => Interlocked.Increment(ref disposed)), TimeSpan.FromSeconds(1), null, boundary),
            new KeyRingProviderFactory(_ => new FakeProvider(dispose: () => Interlocked.Increment(ref disposed)), TimeSpan.FromSeconds(1), null, boundary),
            new KeyRingIsolationControl(
                new KeyRingProviderFactory(_ => new FakeProvider(dispose: () => Interlocked.Increment(ref disposed)), TimeSpan.FromSeconds(1), null, boundary),
                new KeyRingProviderFactory(_ => new FakeProvider(dispose: () => Interlocked.Increment(ref disposed)), TimeSpan.FromSeconds(1), null, boundary)),
            CancellationToken.None);

        await KeyRingVerifier.VerifyAsync(
            KeyRingScenario.RotationContinuity,
            new KeyRingProviderFactory(
                _ => new FakeProvider(dispose: () => Interlocked.Increment(ref disposed)),
                TimeSpan.FromSeconds(1),
                _ => new NullKeyManager(),
                static (manager, activationDate, expirationDate, cancellationToken) =>
                    Task.FromResult(manager.CreateNewKey(activationDate, expirationDate))));

        Assert.Equal(11, disposed);
    }

    [Fact]
    public async Task DiagnosticsDoNotEchoPlaintextProtectedPayloadOrKeyXml()
    {
        byte[]? plaintext = null;
        byte[]? protectedPayload = null;
        const string keyXml = "<key id=\"sensitive\">master material</key>";
        var factory = new KeyRingProviderFactory(
            _ => new FakeProvider(
                protect: value =>
                {
                    plaintext = value.ToArray();
                    protectedPayload = [9, 8, 7, 6];
                    throw new InvalidOperationException($"{keyXml} {Convert.ToBase64String(value)}");
                }),
            TimeSpan.FromSeconds(1));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.Protect, result.FailureKind);
        Assert.NotNull(plaintext);
        Assert.NotNull(protectedPayload);
        Assert.DoesNotContain(keyXml, result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(plaintext!), result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(protectedPayload!), result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("master material", result.Message, StringComparison.Ordinal);
    }

    private static byte[] CreateProtectedPayload(byte[] payload, Guid keyId, bool bigEndian = false)
    {
        var protectedPayload = new byte[20 + payload.Length];
        protectedPayload[0] = 0x09;
        protectedPayload[1] = 0xF0;
        protectedPayload[2] = 0xC9;
        protectedPayload[3] = 0xF0;
        if (bigEndian)
        {
            keyId.TryWriteBytes(protectedPayload.AsSpan(4, 16), bigEndian: true, out _);
        }
        else
        {
            keyId.TryWriteBytes(protectedPayload.AsSpan(4, 16));
        }

        payload.CopyTo(protectedPayload.AsSpan(20));
        return protectedPayload;
    }

    private static byte[] CreateOpaqueProtectedPayload(byte[] payload)
    {
        var protectedPayload = new byte[32 + payload.Length];
        RandomNumberGenerator.Fill(protectedPayload.AsSpan(0, 32));
        payload.CopyTo(protectedPayload.AsSpan(32));
        return protectedPayload;
    }

    private static byte[] UnprotectPayload(byte[] protectedPayload) =>
        protectedPayload.Length >= 20
            && protectedPayload[0] == 0x09
            && protectedPayload[1] == 0xF0
            && protectedPayload[2] == 0xC9
            && protectedPayload[3] == 0xF0
            ? protectedPayload[20..]
            : protectedPayload.ToArray();

    private static byte[] UnprotectOpaquePayload(byte[] protectedPayload) =>
        protectedPayload.Length >= 32
            ? protectedPayload[32..]
            : protectedPayload.ToArray();
}

internal sealed class ReentryProbe
{
    private int _triggered;

    internal ConcurrentBag<KeyRingVerificationResult> NestedResults { get; } = new();

    internal int NestedFactoryInvocations;

    internal void TriggerSync(CancellationToken cancellationToken) =>
        TriggerAsync(cancellationToken).GetAwaiter().GetResult();

    internal async Task TriggerAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _triggered, 1) != 0)
        {
            return;
        }

        var nestedFactory = new KeyRingProviderFactory(
            _ =>
            {
                Interlocked.Increment(ref NestedFactoryInvocations);
                return new FakeProvider();
            },
            TimeSpan.FromSeconds(1));
        NestedResults.Add(
            await KeyRingVerifier.VerifyAsync(
                    KeyRingScenario.RestartContinuity,
                    nestedFactory,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false));
    }
}

internal sealed class NestedOutcomeProbe
{
    internal ConcurrentBag<KeyRingVerificationResult> Results { get; } = new();

    internal int NestedFactoryInvocations;

    internal async Task RunAsync(CancellationToken _)
    {
        var faultedFactory = new KeyRingProviderFactory(
            (Func<CancellationToken, IDataProtectionProvider>)(token =>
            {
                Interlocked.Increment(ref NestedFactoryInvocations);
                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("nested fault");
            }),
            TimeSpan.FromSeconds(1));
        Results.Add(await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, faultedFactory, cancellationToken: CancellationToken.None).ConfigureAwait(false));

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var canceledFactory = new KeyRingProviderFactory(
            _ =>
            {
                Interlocked.Increment(ref NestedFactoryInvocations);
                return new FakeProvider();
            },
            TimeSpan.FromSeconds(1));
        Results.Add(
            await KeyRingVerifier.VerifyAsync(
                    KeyRingScenario.RestartContinuity,
                    canceledFactory,
                    cancellationToken: canceled.Token)
                .ConfigureAwait(false));

        var timedOutFactory = new KeyRingProviderFactory(
            _ =>
            {
                Interlocked.Increment(ref NestedFactoryInvocations);
                Thread.Sleep(100);
                return new FakeProvider();
            },
            TimeSpan.FromMilliseconds(10));
        Results.Add(await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, timedOutFactory, cancellationToken: CancellationToken.None).ConfigureAwait(false));
    }
}

internal static class RotationThreadProbe
{
    [ThreadStatic]
    internal static bool SnapshotSeenOnCurrentThread;
}

internal sealed class ThreadScopedRotationKeyManager : IKeyManager
{
    public TestKey? CreatedKey { get; private set; }

    public IKey CreateNewKey(DateTimeOffset activationDate, DateTimeOffset expirationDate)
    {
        CreatedKey = new TestKey(Guid.NewGuid(), activationDate, expirationDate);
        return CreatedKey;
    }

    public IReadOnlyCollection<IKey> GetAllKeys()
    {
        RotationThreadProbe.SnapshotSeenOnCurrentThread = true;
        return CreatedKey is null ? [] : [CreatedKey];
    }

    public CancellationToken GetCacheExpirationToken() => CancellationToken.None;

    public void RevokeKey(Guid keyId, string? reason) => throw new NotSupportedException();

    public void RevokeAllKeys(DateTimeOffset revocationDate, string? reason) => throw new NotSupportedException();
}

internal sealed class SlowKeyManager : IKeyManager
{
    public IKey CreateNewKey(DateTimeOffset activationDate, DateTimeOffset expirationDate)
    {
        Thread.Sleep(250);
        return null!;
    }

    public IReadOnlyCollection<IKey> GetAllKeys() => [];

    public CancellationToken GetCacheExpirationToken() => CancellationToken.None;

    public void RevokeKey(Guid keyId, string? reason) => throw new NotSupportedException();

    public void RevokeAllKeys(DateTimeOffset revocationDate, string? reason) => throw new NotSupportedException();
}

internal sealed class NullKeyManager : IKeyManager
{
    public IKey CreateNewKey(DateTimeOffset activationDate, DateTimeOffset expirationDate) => null!;

    public IReadOnlyCollection<IKey> GetAllKeys() => [];

    public CancellationToken GetCacheExpirationToken() => CancellationToken.None;

    public void RevokeKey(Guid keyId, string? reason) => throw new NotSupportedException();

    public void RevokeAllKeys(DateTimeOffset revocationDate, string? reason) => throw new NotSupportedException();
}

internal sealed class ExistingKeyManager : IKeyManager
{
    private readonly TestKey _existingKey = new(
        Guid.NewGuid(),
        DateTimeOffset.UtcNow.AddDays(-1),
        DateTimeOffset.UtcNow.AddDays(1));

    public IKey CreateNewKey(DateTimeOffset activationDate, DateTimeOffset expirationDate) => _existingKey;

    public IReadOnlyCollection<IKey> GetAllKeys() => [_existingKey];

    public CancellationToken GetCacheExpirationToken() => CancellationToken.None;

    public void RevokeKey(Guid keyId, string? reason) => throw new NotSupportedException();

    public void RevokeAllKeys(DateTimeOffset revocationDate, string? reason) => throw new NotSupportedException();
}

internal sealed class ThrowingObservationKeyManager : IKeyManager
{
    public IKey CreateNewKey(DateTimeOffset activationDate, DateTimeOffset expirationDate) =>
        new TestKey(Guid.NewGuid(), activationDate, expirationDate);

    public IReadOnlyCollection<IKey> GetAllKeys() => new ThrowingKeyCollection();

    public CancellationToken GetCacheExpirationToken() => CancellationToken.None;

    public void RevokeKey(Guid keyId, string? reason) => throw new NotSupportedException();

    public void RevokeAllKeys(DateTimeOffset revocationDate, string? reason) => throw new NotSupportedException();
}

internal sealed class ThrowingKeyCollection : IReadOnlyCollection<IKey>
{
    public int Count => 1;

    public IEnumerator<IKey> GetEnumerator() => throw new InvalidOperationException("collection failure");

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class MetadataKeyManager : IKeyManager
{
    private readonly string _caseName;
    private TestKey? _createdKey;

    public MetadataKeyManager(string caseName) => _caseName = caseName;

    public IKey CreateNewKey(DateTimeOffset activationDate, DateTimeOffset expirationDate)
    {
        _createdKey = new TestKey(Guid.NewGuid(), activationDate, expirationDate);
        return _createdKey;
    }

    public IReadOnlyCollection<IKey> GetAllKeys()
    {
        if (_createdKey is null)
        {
            return [];
        }

        return _caseName switch
        {
            "duplicate" => [_createdKey, _createdKey],
            "expired" => [new TestKey(_createdKey.KeyId, _createdKey.ActivationDate.AddDays(-2), _createdKey.ActivationDate.AddDays(-1))],
            "not-yet-active" => [new TestKey(_createdKey.KeyId, _createdKey.ExpirationDate.AddDays(1), _createdKey.ExpirationDate.AddDays(2))],
            "revoked" => [new TestKey(_createdKey.KeyId, _createdKey.ActivationDate, _createdKey.ExpirationDate, true)],
            "mismatched" => [new TestKey(Guid.NewGuid(), _createdKey.ActivationDate, _createdKey.ExpirationDate)],
            _ => []
        };
    }

    public CancellationToken GetCacheExpirationToken() => CancellationToken.None;

    public void RevokeKey(Guid keyId, string? reason) => throw new NotSupportedException();

    public void RevokeAllKeys(DateTimeOffset revocationDate, string? reason) => throw new NotSupportedException();
}

internal sealed class TestKey : IKey
{
    public TestKey(Guid keyId, DateTimeOffset activationDate, DateTimeOffset expirationDate, bool isRevoked = false)
    {
        KeyId = keyId;
        CreationDate = activationDate;
        ActivationDate = activationDate;
        ExpirationDate = expirationDate;
        IsRevoked = isRevoked;
    }

    public DateTimeOffset ActivationDate { get; }

    public DateTimeOffset CreationDate { get; }

    public DateTimeOffset ExpirationDate { get; }

    public bool IsRevoked { get; }

    public Guid KeyId { get; }

    public Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption.ConfigurationModel.IAuthenticatedEncryptorDescriptor Descriptor => null!;

    public Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption.IAuthenticatedEncryptor CreateEncryptor() => null!;
}
