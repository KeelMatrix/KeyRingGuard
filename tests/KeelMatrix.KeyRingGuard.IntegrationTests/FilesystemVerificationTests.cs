using KeelMatrix.KeyRingGuard;
using Microsoft.AspNetCore.DataProtection;

namespace KeelMatrix.KeyRingGuard.IntegrationTests;

public sealed class FilesystemVerificationTests
{
    [Fact]
    public async Task RestartContinuitySurvivesIndependentProviderRecreation()
    {
        using var store = new TemporaryKeyStore();

        var result = await KeyRingVerifier.VerifyAsync(
            KeyRingScenario.RestartContinuity,
            store.CreateFactory("Orders.App"));

        Assert.True(result.Succeeded, result.Message);
    }

    [Fact]
    public async Task ReplicaSharingWorksInBothDirections()
    {
        using var store = new TemporaryKeyStore();

        var result = await KeyRingVerifier.VerifyAsync(
            KeyRingScenario.ReplicaSharing,
            store.CreateFactory("Orders.App"),
            store.CreateFactory("Orders.App"));

        Assert.True(result.Succeeded, result.Message);
    }

    [Fact]
    public async Task DifferentApplicationNamesAreIsolated()
    {
        using var store = new TemporaryKeyStore();
        var boundary = new KeyRingBoundary();

        var result = await KeyRingVerifier.VerifyWithIsolationControlAsync(
            KeyRingScenario.ApplicationIsolation,
            store.CreateFactory("Orders.App", boundary),
            store.CreateFactory("Billing.App", boundary),
            new KeyRingIsolationControl(
                store.CreateFactory("Orders.App", boundary),
                store.CreateFactory("Orders.App", boundary)),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
    }

    [Fact]
    public async Task ApplicationIsolationDoesNotPassForSeparateStores()
    {
        using var firstStore = new TemporaryKeyStore();
        using var secondStore = new TemporaryKeyStore();
        using var controlStore = new TemporaryKeyStore();
        var boundary = new KeyRingBoundary();
        var controlBoundary = new KeyRingBoundary();

        var result = await KeyRingVerifier.VerifyWithIsolationControlAsync(
            KeyRingScenario.ApplicationIsolation,
            firstStore.CreateFactory("Orders.App", boundary),
            secondStore.CreateFactory("Billing.App", boundary),
            new KeyRingIsolationControl(
                controlStore.CreateFactory("Orders.App", controlBoundary),
                controlStore.CreateFactory("Orders.App", controlBoundary)),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.InvalidScenario, result.FailureKind);
    }

    [Fact]
    public async Task ApplicationIsolationRejectsAnUnrelatedSharedBoundaryControl()
    {
        using var firstStore = new TemporaryKeyStore();
        using var secondStore = new TemporaryKeyStore();
        using var unrelatedStore = new TemporaryKeyStore();
        var mainBoundary = new KeyRingBoundary();
        var unrelatedBoundary = new KeyRingBoundary();

        var result = await KeyRingVerifier.VerifyWithIsolationControlAsync(
            KeyRingScenario.ApplicationIsolation,
            firstStore.CreateFactory("Orders.App", mainBoundary),
            secondStore.CreateFactory("Billing.App", mainBoundary),
            new KeyRingIsolationControl(
                unrelatedStore.CreateFactory("Orders.App", unrelatedBoundary),
                unrelatedStore.CreateFactory("Orders.App", unrelatedBoundary)),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.InvalidScenario, result.FailureKind);
    }

    [Fact]
    public async Task DifferentPurposesAreIsolated()
    {
        using var store = new TemporaryKeyStore();

        var result = await KeyRingVerifier.VerifyAsync(
            KeyRingScenario.PurposeIsolation,
            store.CreateFactory("Orders.App"),
            store.CreateFactory("Orders.App"));

        Assert.True(result.Succeeded, result.Message);
    }

    [Fact]
    public async Task PurposeIsolationDoesNotPassForPerCallStores()
    {
        using var firstStore = new TemporaryKeyStore();
        using var secondStore = new TemporaryKeyStore();
        var factoryCalls = 0;
        var factory = new KeyRingProviderFactory(
            _ =>
            {
                var store = Interlocked.Increment(ref factoryCalls) % 2 == 1 ? firstStore : secondStore;
                return DataProtectionProvider.Create(
                    new DirectoryInfo(store.Path),
                    builder => builder.SetApplicationName("Orders.App"));
            },
            TimeSpan.FromSeconds(5));

        var result = await KeyRingVerifier.VerifyAsync(
            KeyRingScenario.PurposeIsolation,
            factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.Unprotect, result.FailureKind);
    }

    [Fact]
    public async Task DeterministicRotationPreservesAnOlderPayload()
    {
        using var store = new TemporaryKeyStore();

        var result = await KeyRingVerifier.VerifyAsync(
            KeyRingScenario.RotationContinuity,
            store.CreateRotationFactory("Orders.App"));

        Assert.True(result.Succeeded, result.Message);
    }

    [Fact]
    public async Task RotationRejectsAKeyManagerFromAnotherStore()
    {
        using var providerStore = new TemporaryKeyStore();
        using var managerStore = new TemporaryKeyStore();

        var managerFactory = managerStore.CreateKeyManagerFactory("Orders.App");
        var result = await KeyRingVerifier.VerifyAsync(
            KeyRingScenario.RotationContinuity,
            providerStore.CreateRotationFactory("Orders.App", managerFactory));

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.RotationFailure, result.FailureKind);
    }

    [Fact]
    public async Task TemporaryStoreIsRemovedAfterTestOwnershipEnds()
    {
        string path;
        using (var store = new TemporaryKeyStore())
        {
            path = store.Path;
            var result = await KeyRingVerifier.VerifyAsync(
                KeyRingScenario.RestartContinuity,
                store.CreateFactory("Cleanup.App"));
            Assert.True(result.Succeeded, result.Message);
        }

        Assert.False(Directory.Exists(path));
    }
}
