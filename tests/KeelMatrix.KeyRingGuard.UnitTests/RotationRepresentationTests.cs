using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;

namespace KeelMatrix.KeyRingGuard.UnitTests;

[Collection("Provider lifecycle")]
public sealed class RotationRepresentationTests
{
    private static readonly Guid OldKeyId = Guid.Parse("98765432-9876-4987-8abc-0123456789ab");

    public static IEnumerable<object[]> Representations()
    {
        foreach (var first in new[] { "12343412", "12343413" })
            foreach (var second in new[] { "5656", "5657" })
                foreach (var third in new[] { "4a4a", "4a4b" })
                    foreach (var tail in new[] { "8abc-0123456789ab", "8def-ffffffffffff" })
                        foreach (var asynchronous in new[] { false, true })
                            foreach (var bigEndian in new[] { false, true })
                            {
                                yield return [$"{first}-{second}-{third}-{tail}", asynchronous, bigEndian];
                            }

        foreach (var id in new[]
        {
            "12353412-5656-4a4a-8abc-0123456789ab", // Only the inner pair of the first field differs.
            "12345678-1234-4567-8abc-0123456789ab",
            "12345678-1234-4546-8abc-0123456789ab",
            "78563412-3412-4645-8abc-0123456789ab",
            "00000000-0000-0000-0000-000000000000",
            "ffffffff-ffff-ffff-ffff-ffffffffffff",
        })
            foreach (var asynchronous in new[] { false, true })
                foreach (var bigEndian in new[] { false, true })
                {
                    yield return [id, asynchronous, bigEndian];
                }
    }

    [Theory]
    [InlineData(false, "old")]
    [InlineData(false, "new")]
    [InlineData(true, "old")]
    [InlineData(true, "new")]
    public async Task RotationNeverLetsProtectRewriteEitherExpectedCanary(bool asynchronous, string phase)
    {
        var id = Guid.Parse("12345678-1234-4546-8abc-0123456789ab");
        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity,
            CreateFactory(id, asynchronous, payload => Envelope(payload, id), mutation: phase));
        Assert.Equal(KeyRingFailureKind.Unprotect, result.FailureKind);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DistinctSwappedStaleIdsFailAdoptionInEitherRole(bool asynchronous, bool reverse)
    {
        var expected = Guid.Parse(reverse ? "78563412-3412-4645-8abc-0123456789ab" : "12345678-1234-4546-8abc-0123456789ab");
        var stale = new Guid(expected.ToByteArray(bigEndian: true));
        Assert.NotEqual(expected, stale);
        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity,
            CreateFactory(expected, asynchronous, payload => Envelope(payload, stale), oldId: stale));
        Assert.Equal(KeyRingFailureKind.RotationFailure, result.FailureKind);
        Assert.Contains("did not adopt", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Representations))]
    public async Task RotationInterpretsEveryStructurallyValidIdCanonically(
        string id, bool asynchronous, bool bigEndian)
    {
        var keyId = Guid.Parse(id);
        var factory = CreateFactory(keyId, asynchronous, payload => Envelope(payload, keyId, bigEndian));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory);

        var canonical = !bigEndian || keyId.ToByteArray().SequenceEqual(keyId.ToByteArray(bigEndian: true));
        Assert.Equal(canonical, result.Succeeded);
        Assert.Equal(canonical ? KeyRingFailureKind.None : KeyRingFailureKind.RotationFailure, result.FailureKind);
    }

    public static IEnumerable<object[]> InvalidEnvelopes()
    {
        foreach (var id in new[]
        {
            "12343412-5656-4a4a-8abc-0123456789ab",
            "12345678-1234-4567-8abc-0123456789ab",
            "00000000-0000-0000-0000-000000000000",
            "ffffffff-ffff-ffff-ffff-ffffffffffff",
        })
            foreach (var asynchronous in new[] { false, true })
                foreach (var damage in new[] { "length-0", "length-3", "length-4", "length-5", "length-19", "length-20", "header-0", "header-1", "header-2", "header-3", "opaque" })
                {
                    yield return [id, asynchronous, damage];
                }
    }

    [Theory]
    [MemberData(nameof(InvalidEnvelopes))]
    public async Task RotationStillRequiresTheCompleteHeaderIdAndNonemptyBody(string id, bool asynchronous, string damage)
    {
        var keyId = Guid.Parse(id);
        var factory = CreateFactory(keyId, asynchronous, payload =>
        {
            var envelope = Envelope(payload, keyId);
            if (damage.StartsWith("length-", StringComparison.Ordinal))
            {
                return envelope[..int.Parse(damage.AsSpan(7), System.Globalization.CultureInfo.InvariantCulture)];
            }

            if (damage == "opaque")
            {
                return Enumerable.Repeat((byte)0xA5, payload.Length).ToArray();
            }

            envelope[int.Parse(damage.AsSpan(7), System.Globalization.CultureInfo.InvariantCulture)] ^= 1;
            return envelope;
        });

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.RotationUnavailable, result.FailureKind);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RotationStillRejectsStaleIdsAndIdsDifferingOnlyInTheUnswappedTail(bool asynchronous, bool stale)
    {
        var keyId = Guid.Parse("12343412-5656-4a4a-8abc-0123456789ab");
        var payloadId = stale ? OldKeyId : Guid.Parse("12343412-5656-4a4a-8abc-0123456789ac");
        var factory = CreateFactory(keyId, asynchronous, payload => Envelope(payload, payloadId));

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.RotationFailure, result.FailureKind);
        Assert.Contains("did not adopt", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task CanonicalSymmetricIdsStillRequireBothOldAndNewCanariesToMatch(bool asynchronous, int brokenRead)
    {
        var keyId = Guid.Parse("12343412-5656-4a4a-8abc-0123456789ab");
        var factory = CreateFactory(keyId, asynchronous, payload => Envelope(payload, keyId), brokenRead);

        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory);

        Assert.False(result.Succeeded);
        Assert.Equal(KeyRingFailureKind.Unprotect, result.FailureKind);
    }

    private static KeyRingProviderFactory CreateFactory(Guid keyId, bool asynchronous, Func<byte[], byte[]> protect, int brokenRead = 0, string mutation = "", Guid? oldId = null)
    {
        var manager = new KnownIdManager(keyId, oldId);
        var reads = 0;
        IDataProtectionProvider Provider() => new FakeProvider(
            protect: payload =>
            {
                if ((mutation == "old" && manager.CreatedKey is null) || (mutation == "new" && manager.CreatedKey is not null)) { payload[0] ^= 1; }
                return manager.CreatedKey is null ? Envelope(payload, oldId ?? OldKeyId) : protect(payload);
            },
            unprotect: payload => ++reads == brokenRead ? [] : payload[20..]);

        return asynchronous
            ? new KeyRingProviderFactory(
                async _ => { await Task.Yield(); return Provider(); },
                TimeSpan.FromSeconds(2),
                async _ => { await Task.Yield(); return manager; },
                async (m, activation, expiration, token) =>
                {
                    await Task.Yield();
                    token.ThrowIfCancellationRequested();
                    return m.CreateNewKey(activation, expiration);
                })
            : new KeyRingProviderFactory(
                _ => Provider(),
                TimeSpan.FromSeconds(2),
                _ => manager,
                (m, activation, expiration, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(m.CreateNewKey(activation, expiration));
                });
    }

    private static byte[] Envelope(byte[] payload, Guid keyId, bool bigEndian = false)
    {
        byte[] envelope = [0x09, 0xF0, 0xC9, 0xF0, .. new byte[16], .. payload];
        keyId.TryWriteBytes(envelope.AsSpan(4, 16), bigEndian, out _);
        return envelope;
    }

    private sealed class KnownIdManager(Guid keyId, Guid? oldId) : IKeyManager
    {
        private readonly IKey _oldKey = new TestKey(oldId ?? OldKeyId, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        public IKey? CreatedKey { get; private set; }
        public IKey CreateNewKey(DateTimeOffset activationDate, DateTimeOffset expirationDate) =>
            CreatedKey = new TestKey(keyId, activationDate, expirationDate);
        public IReadOnlyCollection<IKey> GetAllKeys() => CreatedKey is null ? [_oldKey] : [_oldKey, CreatedKey];
        public CancellationToken GetCacheExpirationToken() => CancellationToken.None;
        public void RevokeKey(Guid keyId, string? reason) => throw new NotSupportedException();
        public void RevokeAllKeys(DateTimeOffset revocationDate, string? reason) => throw new NotSupportedException();
    }
}
