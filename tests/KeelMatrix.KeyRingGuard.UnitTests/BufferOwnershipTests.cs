using Microsoft.AspNetCore.DataProtection;

namespace KeelMatrix.KeyRingGuard.UnitTests;

[Collection("Provider lifecycle")]
public sealed class BufferOwnershipTests
{
    public static IEnumerable<object[]> Mutations()
    {
        foreach (var asynchronous in new[] { false, true })
            foreach (var scenario in new[] { "restart", "replica", "purpose" })
                yield return [asynchronous, scenario];
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    public async Task ApplicationAndControlProtectorsCannotRewriteExpectedEvidence(bool asynchronous, int mutatedSide)
    {
        var boundary = new KeyRingBoundary();
        KeyRingProviderFactory Factory(int side)
        {
            IDataProtectionProvider Create() => new FakeProvider(protect: input => { if (side == mutatedSide) { input[0] ^= 1; } return input; });
            return asynchronous
                ? new(async _ => { await Task.Yield(); return Create(); }, TimeSpan.FromSeconds(2), null, boundary)
                : new(_ => Create(), TimeSpan.FromSeconds(2), null, boundary);
        }
        var result = await KeyRingVerifier.VerifyWithIsolationControlAsync(KeyRingScenario.ApplicationIsolation,
            Factory(0), Factory(1), new KeyRingIsolationControl(Factory(2), Factory(3)), CancellationToken.None);
        Assert.Equal(KeyRingFailureKind.Unprotect, result.FailureKind);
    }

    [Theory]
    [MemberData(nameof(Mutations))]
    public async Task ProtectCannotRewriteTheExpectedCanary(bool asynchronous, string scenario)
    {
        var factory = Factory(asynchronous, () => new FakeProvider(protect: input =>
        {
            input[0] ^= 1;
            return input;
        }));
        var result = await KeyRingVerifier.VerifyAsync(Scenario(scenario), factory, factory);
        Assert.Equal(KeyRingFailureKind.Unprotect, result.FailureKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EachReplicaDirectionKeepsItsOwnProtectedSnapshot(bool asynchronous)
    {
        var shared = new byte[32];
        var factory = Factory(asynchronous, () => new FakeProvider(protect: input =>
        {
            input.CopyTo(shared, 0);
            return shared;
        }, unprotect: input =>
        {
            var result = input.ToArray();
            Array.Clear(input);
            Array.Clear(shared);
            return result;
        }));
        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.ReplicaSharing, factory, factory);
        Assert.True(result.Succeeded, result.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshotPrecedesProviderRetirement(bool asynchronous)
    {
        byte[]? returned = null;
        var factory = Factory(asynchronous, () => new FakeProvider(
            protect: input => returned = input,
            dispose: () => { if (returned is not null) { Array.Clear(returned); } }));
        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RestartContinuity, factory);
        Assert.True(result.Succeeded, result.Message);
    }

    internal static KeyRingProviderFactory Factory(bool asynchronous, Func<IDataProtectionProvider> create, TimeSpan? timeout = null) => asynchronous
        ? new(async _ => { await Task.Yield(); return create(); }, timeout ?? TimeSpan.FromSeconds(2))
        : new(_ => create(), timeout ?? TimeSpan.FromSeconds(2));

    private static KeyRingScenario Scenario(string value) => value switch
    {
        "restart" => KeyRingScenario.RestartContinuity,
        "replica" => KeyRingScenario.ReplicaSharing,
        _ => KeyRingScenario.PurposeIsolation,
    };
}
