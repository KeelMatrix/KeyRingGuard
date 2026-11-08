using System.Collections.Concurrent;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;

namespace KeelMatrix.KeyRingGuard.UnitTests;

public sealed partial class VerifierFailureTests
{
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
                    "Verification cannot be started from a provider, key-manager, rotation, or cleanup callback.",
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
                "Verification cannot be started from a provider, key-manager, rotation, or cleanup callback.",
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
                "Verification cannot be started from a provider, key-manager, rotation, or cleanup callback.",
                nestedResult.Message);
        });
        Assert.Equal(0, Volatile.Read(ref probe.NestedFactoryInvocations));
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
}
