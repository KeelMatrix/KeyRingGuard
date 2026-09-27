using Microsoft.AspNetCore.DataProtection;

namespace KeelMatrix.KeyRingGuard.UnitTests;

internal sealed class FakeProvider : IDataProtectionProvider, IDisposable
{
    private readonly Func<byte[], byte[]> _unprotect;
    private readonly Func<byte[], byte[]>? _protect;
    private readonly Action? _dispose;

    public FakeProvider(
        Func<byte[], byte[]>? protect = null,
        Func<byte[], byte[]>? unprotect = null,
        Action? dispose = null)
    {
        _protect = protect;
        _unprotect = unprotect ?? (payload => payload);
        _dispose = dispose;
    }

    public IDataProtector CreateProtector(string purpose) => new FakeProtector(_protect, _unprotect);

    public void Dispose() => _dispose?.Invoke();
}

internal sealed class FakeProtector : IDataProtector
{
    private readonly Func<byte[], byte[]>? _protect;
    private readonly Func<byte[], byte[]> _unprotect;

    public FakeProtector(Func<byte[], byte[]>? protect, Func<byte[], byte[]> unprotect)
    {
        _protect = protect;
        _unprotect = unprotect;
    }

    public byte[] Protect(byte[] plaintext) => _protect?.Invoke(plaintext) ?? plaintext.ToArray();

    public byte[] Unprotect(byte[] protectedData) => _unprotect(protectedData);

    public IDataProtector CreateProtector(string purpose) => this;
}
