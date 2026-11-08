using Microsoft.AspNetCore.DataProtection.KeyManagement;

namespace KeelMatrix.KeyRingGuard.UnitTests;

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
