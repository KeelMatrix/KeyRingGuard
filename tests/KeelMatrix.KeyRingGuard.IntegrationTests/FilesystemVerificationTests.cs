using KeelMatrix.KeyRingGuard;

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

        var result = await KeyRingVerifier.VerifyAsync(
            KeyRingScenario.ApplicationIsolation,
            store.CreateFactory("Orders.App"),
            store.CreateFactory("Billing.App"));

        Assert.True(result.Succeeded, result.Message);
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
