using Microsoft.AspNetCore.DataProtection.KeyManagement;

namespace KeelMatrix.KeyRingGuard.UnitTests;

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
