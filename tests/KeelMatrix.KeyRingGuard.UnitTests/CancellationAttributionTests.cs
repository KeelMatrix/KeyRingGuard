using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;

namespace KeelMatrix.KeyRingGuard.UnitTests;

[Collection("Provider lifecycle")]
public sealed class CancellationAttributionTests
{
    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(false, false, 1)]
    [InlineData(false, true, 0)]
    [InlineData(false, true, 1)]
    [InlineData(true, false, 0)]
    [InlineData(true, false, 1)]
    [InlineData(true, true, 0)]
    [InlineData(true, true, 1)]
    public async Task PersistedProtectorCancellationIsNeverAnIsolationRejection(bool asynchronous, bool purpose, int canceledSide)
    {
        var boundary = new KeyRingBoundary();
        var nextSide = 0;
        KeyRingProviderFactory Factory(int side, bool control = false)
        {
            IDataProtectionProvider Create()
            {
                var reads = 0;
                var actualSide = purpose ? Interlocked.Increment(ref nextSide) - 1 : side;
                return control ? new FakeProvider() : new FakeProvider(createProtector: _ => new CancelingProtector(input =>
                {
                    if (Interlocked.Increment(ref reads) <= (purpose ? 2 : 1)) { return input; }
                    throw actualSide == canceledSide ? new OperationCanceledException("private-cross-stop") : new System.Security.Cryptography.CryptographicException();
                }));
            }
            return asynchronous
                ? new(async _ => { await Task.Yield(); return Create(); }, TimeSpan.FromHours(1), null, boundary)
                : new(_ => Create(), TimeSpan.FromHours(1), null, boundary);
        }
        var result = purpose
            ? await KeyRingVerifier.VerifyAsync(KeyRingScenario.PurposeIsolation, Factory(0))
            : await KeyRingVerifier.VerifyWithIsolationControlAsync(KeyRingScenario.ApplicationIsolation, Factory(0), Factory(1),
                new KeyRingIsolationControl(Factory(2, control: true), Factory(3, control: true)), CancellationToken.None);
        Assert.Equal(KeyRingFailureKind.Unprotect, result.FailureKind);
        Assert.DoesNotContain("private-cross-stop", result.Message, StringComparison.Ordinal);
    }

    private sealed class CancelingProtector(Func<byte[], byte[]> unprotect) : IPersistedDataProtector
    {
        public IDataProtector CreateProtector(string purpose) => this;
        public byte[] Protect(byte[] plaintext) => plaintext;
        public byte[] Unprotect(byte[] protectedData) => unprotect(protectedData);
        public byte[] DangerousUnprotect(byte[] protectedData, bool ignoreRevocationErrors, out bool requiresMigration, out bool wasRevoked)
        { requiresMigration = false; wasRevoked = false; return Unprotect(protectedData); }
    }

    public static IEnumerable<object[]> IndependentStops()
    {
        foreach (var site in new[] { "provider", "manager", "protector", "protect", "unprotect" })
            foreach (var form in new[] { "sync", "delegate-throw", "async-throw", "faulted", "canceled" })
                foreach (var token in new[] { "default", "unrelated", "supplied" })
                    if ((site is "provider" or "manager" || form == "sync") && (form != "canceled" || token == "unrelated")) { yield return [site, form, token]; }
    }

    [Theory]
    [MemberData(nameof(IndependentStops))]
    public async Task CallbackCancellationIsAnOperationFailure(string site, string form, string tokenKind)
    {
        using var independent = new CancellationTokenSource();
        independent.Cancel();
        var timeout = TimeSpan.FromHours(1);
        OperationCanceledException Error(CancellationToken supplied) => new("private-callback-stop", tokenKind switch
        {
            "unrelated" => independent.Token,
            "supplied" => supplied,
            _ => CancellationToken.None,
        });
        Task<T> Fail<T>(CancellationToken token) => form switch
        {
            "async-throw" => ThrowAsync<T>(token),
            "faulted" => Task.FromException<T>(Error(token)),
            "canceled" => Task.FromCanceled<T>(independent.Token),
            _ => throw Error(token),
        };
        async Task<T> ThrowAsync<T>(CancellationToken token) { await Task.Yield(); throw Error(token); }
        IDataProtectionProvider Provider(CancellationToken token) => site == "provider" ? throw Error(token) : new FakeProvider(
            createProtector: site == "protector" ? _ => throw Error(token) : null,
            protect: site == "protect" ? _ => throw Error(token) : null,
            unprotect: site == "unprotect" ? _ => throw Error(token) : null);
        var factory = form == "sync"
            ? new KeyRingProviderFactory(Provider, timeout, token => throw Error(token),
                (_, _, _, token) => Fail<IKey>(token))
            : new KeyRingProviderFactory(
                token => site == "provider" ? Fail<IDataProtectionProvider>(token) : Task.FromResult(Provider(token)),
                timeout, token => Fail<IKeyManager>(token), (_, _, _, token) => Fail<IKey>(token));
        var result = await KeyRingVerifier.VerifyAsync(site == "manager" ? KeyRingScenario.RotationContinuity : KeyRingScenario.RestartContinuity, factory)
            .WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(site switch
        {
            "provider" => KeyRingFailureKind.ProviderCreation,
            "manager" => KeyRingFailureKind.RotationFailure,
            "unprotect" => KeyRingFailureKind.Unprotect,
            _ => KeyRingFailureKind.Protect,
        }, result.FailureKind);
        Assert.DoesNotContain("private-callback-stop", result.Message, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> FactoryRoutes()
    {
        foreach (var route in new[] { "primary", "recreated", "secondary", "control-primary", "control-secondary" })
            foreach (var asynchronous in new[] { false, true })
                foreach (var canceledTask in asynchronous ? new[] { false, true } : new[] { false })
                    yield return [route, asynchronous, canceledTask];
    }

    [Theory]
    [MemberData(nameof(FactoryRoutes))]
    public async Task IndependentCancellationHasTheSameMeaningAtEveryFactoryRoute(string route, bool asynchronous, bool canceledTask)
    {
        using var unrelated = new CancellationTokenSource();
        unrelated.Cancel();
        var boundary = new KeyRingBoundary();
        var primaryCalls = 0;
        KeyRingProviderFactory Factory(string position)
        {
            bool Fails() => position == route || (position == "primary" && route == "recreated" && ++primaryCalls == 2);
            IDataProtectionProvider Create() => Fails() ? throw new OperationCanceledException("private-route", unrelated.Token) : new FakeProvider();
            return asynchronous
                ? new(_ =>
                {
                    if (!Fails()) { return Task.FromResult<IDataProtectionProvider>(new FakeProvider()); }
                    return canceledTask ? Task.FromCanceled<IDataProtectionProvider>(unrelated.Token)
                        : throw new OperationCanceledException("private-route", unrelated.Token);
                }, TimeSpan.FromHours(1), null, boundary)
                : new(_ => Create(), TimeSpan.FromHours(1), null, boundary);
        }
        var first = Factory("primary");
        var result = route.StartsWith("control", StringComparison.Ordinal)
            ? await KeyRingVerifier.VerifyWithIsolationControlAsync(KeyRingScenario.ApplicationIsolation, first, Factory("secondary"),
                new KeyRingIsolationControl(Factory("control-primary"), Factory("control-secondary")), CancellationToken.None)
            : await KeyRingVerifier.VerifyAsync(route == "secondary" ? KeyRingScenario.ReplicaSharing : KeyRingScenario.RestartContinuity, first, Factory("secondary"));
        Assert.Equal(KeyRingFailureKind.ProviderCreation, result.FailureKind);
        Assert.DoesNotContain("private-route", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "snapshot", false)]
    [InlineData(false, "observation", false)]
    [InlineData(false, "rotation", false)]
    [InlineData(false, "rotation", true)]
    [InlineData(true, "snapshot", false)]
    [InlineData(true, "observation", false)]
    [InlineData(true, "rotation", false)]
    [InlineData(true, "rotation", true)]
    public async Task IndependentCancellationDuringRotationRemainsRotationFailure(bool asynchronous, string site, bool canceledTask)
    {
        using var unrelated = new CancellationTokenSource();
        unrelated.Cancel();
        var manager = new CancelingManager(site, unrelated.Token);
        Task<IKey> Rotate(IKeyManager value, DateTimeOffset activation, DateTimeOffset expiration, CancellationToken token) => site == "rotation"
            ? canceledTask ? Task.FromCanceled<IKey>(unrelated.Token) : throw new OperationCanceledException("private-rotation", token)
            : Task.FromResult(value.CreateNewKey(activation, expiration));
        var factory = asynchronous
            ? new KeyRingProviderFactory(async _ => { await Task.Yield(); return new FakeProvider(); }, TimeSpan.FromHours(1),
                async _ => { await Task.Yield(); return manager; }, Rotate)
            : new KeyRingProviderFactory(_ => new FakeProvider(), TimeSpan.FromHours(1), _ => manager, Rotate);
        var result = await KeyRingVerifier.VerifyAsync(KeyRingScenario.RotationContinuity, factory).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(KeyRingFailureKind.RotationFailure, result.FailureKind);
        Assert.DoesNotContain("private-", result.Message, StringComparison.Ordinal);
    }

    private sealed class CancelingManager(string site, CancellationToken token) : IKeyManager
    {
        private readonly List<IKey> _keys = [new TestKey(Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1))];
        public IReadOnlyCollection<IKey> GetAllKeys() => site == "snapshot" || (site == "observation" && _keys.Count > 1)
            ? throw new OperationCanceledException("private-observation", token) : _keys.ToArray();
        public IKey CreateNewKey(DateTimeOffset activationDate, DateTimeOffset expirationDate)
        {
            var key = new TestKey(Guid.NewGuid(), activationDate, expirationDate);
            _keys.Add(key);
            return key;
        }
        public CancellationToken GetCacheExpirationToken() => CancellationToken.None;
        public void RevokeKey(Guid keyId, string? reason) => throw new NotSupportedException();
        public void RevokeAllKeys(DateTimeOffset revocationDate, string? reason) => throw new NotSupportedException();
    }
}
