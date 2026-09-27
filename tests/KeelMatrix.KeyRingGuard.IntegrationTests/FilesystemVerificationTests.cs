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

        var result = await KeyRingVerifier.VerifyWithIsolationControlAsync(
            KeyRingScenario.ApplicationIsolation,
            store.CreateFactory("Orders.App"),
            store.CreateFactory("Billing.App"),
            new KeyRingIsolationControl(
                store.CreateFactory("Orders.App"),
                store.CreateFactory("Orders.App")),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
    }

    [Fact]
    public async Task ApplicationIsolationDoesNotPassForSeparateStores()
    {
        using var firstStore = new TemporaryKeyStore();
        using var secondStore = new TemporaryKeyStore();

        var result = await KeyRingVerifier.VerifyWithIsolationControlAsync(
            KeyRingScenario.ApplicationIsolation,
            firstStore.CreateFactory("Orders.App"),
            secondStore.CreateFactory("Billing.App"),
            new KeyRingIsolationControl(
                firstStore.CreateFactory("Orders.App"),
                secondStore.CreateFactory("Orders.App")),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.Unprotect, result.FailureKind);
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
