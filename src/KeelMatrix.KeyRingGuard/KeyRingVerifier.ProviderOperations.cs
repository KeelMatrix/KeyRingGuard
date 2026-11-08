using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace KeelMatrix.KeyRingGuard;

public static partial class KeyRingVerifier
{
    private static async Task<(IDataProtector? Value, KeyRingVerificationResult? Failure)> CreateProtectorAsync(
        KeyRingScenario scenario,
        IDataProtectionProvider provider,
        string purpose,
        TimeSpan timeout,
        ProviderScope providerScope,
        CancellationToken cancellationToken)
    {
        var operation = await ExecuteBoundedAsync(
            () => provider.CreateProtector(purpose),
            timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (operation.TimedOut)
        {
            return (null, Failure(scenario, KeyRingFailureKind.Timeout, "Protector creation timed out before the configured bound."));
        }
        if (operation.Canceled)
        {
            return (null, Failure(scenario, KeyRingFailureKind.Canceled, "Verification was canceled."));
        }
        if (operation.Exception is not null || operation.Value is null)
        {
            return (null, Failure(scenario, KeyRingFailureKind.Protect, "Protect failed: the configured provider could not create a protector."));
        }

        return (operation.Value, null);
    }

    private static async Task<(byte[]? Value, KeyRingVerificationResult? Failure)> ProtectAsync(
        KeyRingScenario scenario,
        IDataProtector protector,
        byte[] canary,
        TimeSpan timeout,
        ProviderScope providerScope,
        CancellationToken cancellationToken)
    {
        var operation = await ExecuteBoundedAsync(
            () => protector.Protect(canary.ToArray())?.ToArray(),
            timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (operation.TimedOut)
        {
            return (null, Failure(scenario, KeyRingFailureKind.Timeout, "Protect timed out before the configured bound."));
        }
        if (operation.Canceled)
        {
            return (null, Failure(scenario, KeyRingFailureKind.Canceled, "Verification was canceled."));
        }
        if (operation.Exception is not null || operation.Value is null)
        {
            return (null, Failure(scenario, KeyRingFailureKind.Protect, "Protect failed: the configured provider could not protect the synthetic canary."));
        }

        return (operation.Value, null);
    }

    private static async Task<(byte[]? Value, KeyRingVerificationResult? Failure)> ProtectWithPurposeAsync(
        KeyRingScenario scenario,
        IDataProtectionProvider provider,
        byte[] canary,
        string purpose,
        TimeSpan timeout,
        ProviderScope providerScope,
        CancellationToken cancellationToken)
    {
        var protectorResult = await CreateProtectorAsync(
            scenario,
            provider,
            purpose,
            timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (protectorResult.Failure is not null)
        {
            return (null, protectorResult.Failure);
        }

        return await ProtectAsync(
            scenario,
            protectorResult.Value!,
            canary,
            timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<KeyRingVerificationResult> UnprotectAndCompareAsync(
        KeyRingScenario scenario,
        IDataProtectionProvider provider,
        byte[] protectedPayload,
        byte[] canary,
        string purpose,
        TimeSpan timeout,
        ProviderScope providerScope,
        CancellationToken cancellationToken)
    {
        var protectorResult = await CreateProtectorAsync(
            scenario,
            provider,
            purpose,
            timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (protectorResult.Failure is not null)
        {
            return protectorResult.Failure.FailureKind == KeyRingFailureKind.Protect
                ? Failure(scenario, KeyRingFailureKind.Unprotect, "Unprotect failed: the configured provider could not create a protector.")
                : protectorResult.Failure;
        }

        var operation = await ExecuteBoundedAsync(
            () => protectorResult.Value!.Unprotect(protectedPayload.ToArray())?.ToArray(),
            timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (operation.TimedOut)
        {
            return Failure(scenario, KeyRingFailureKind.Timeout, "Unprotect timed out before the configured bound.");
        }
        if (operation.Canceled)
        {
            return Failure(scenario, KeyRingFailureKind.Canceled, "Verification was canceled.");
        }
        if (operation.Exception is not null || operation.Value is null)
        {
            return Failure(scenario, KeyRingFailureKind.Unprotect, "Unprotect failed: a payload that should survive the provider transition could not be read.");
        }

        return CryptographicOperations.FixedTimeEquals(operation.Value, canary)
            ? Success(scenario, "Continuity passed.")
            : Failure(scenario, KeyRingFailureKind.Unprotect, "Unprotect failed: the provider returned an unexpected payload.");
    }

    private static async Task<KeyRingVerificationResult> ExpectUnprotectFailureAsync(
        KeyRingScenario scenario,
        IDataProtectionProvider provider,
        byte[] protectedPayload,
        string purpose,
        TimeSpan timeout,
        ProviderScope providerScope,
        CancellationToken cancellationToken)
    {
        var protectorResult = await CreateProtectorAsync(
            scenario,
            provider,
            purpose,
            timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (protectorResult.Failure is not null)
        {
            return protectorResult.Failure.FailureKind == KeyRingFailureKind.Protect
                ? Failure(scenario, KeyRingFailureKind.Unprotect, "Unprotect failed: the configured provider could not create a protector.")
                : protectorResult.Failure;
        }

        var operation = await ExecuteBoundedAsync(
            () => protectorResult.Value!.Unprotect(protectedPayload.ToArray())?.ToArray(),
            timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (operation.TimedOut)
        {
            return Failure(scenario, KeyRingFailureKind.Timeout, "Isolation check timed out before the configured bound.");
        }
        if (operation.Canceled)
        {
            return Failure(scenario, KeyRingFailureKind.Canceled, "Verification was canceled.");
        }
        if (operation.Exception is CryptographicException
            && protectorResult.Value is IPersistedDataProtector)
        {
            return Success(scenario, "Isolation passed.");
        }
        if (operation.Exception is not null)
        {
            return Failure(
                scenario,
                KeyRingFailureKind.Unprotect,
                "Isolation failed: the cross-boundary rejection was not attributable to the standard ASP.NET Core persisted-protector contract.");
        }

        return Failure(scenario, KeyRingFailureKind.UnexpectedCrossUnprotect, "Isolation failed: a provider from an isolated boundary successfully unprotected a payload.");
    }

    private static async Task<(IDataProtectionProvider? Provider, KeyRingVerificationResult? Failure)> CreateProviderAsync(
        KeyRingScenario scenario,
        KeyRingProviderFactory factory,
        ProviderScope providerScope,
        CancellationToken cancellationToken)
    {
        var operation = await ExecuteOwnedAsync(
            factory.CreateProviderAsync, factory.Timeout, providerScope, cancellationToken,
            provider => { if (provider is not null) { providerScope.Add(provider); } }).ConfigureAwait(false);
        if (operation.Canceled)
        {
            return (null, Failure(scenario, KeyRingFailureKind.Canceled, "Verification was canceled."));
        }
        if (operation.TimedOut)
        {
            return (null, Failure(scenario, KeyRingFailureKind.Timeout, "Provider creation timed out before the configured bound."));
        }
        if (operation.Exception is not null)
        {
            return (null, Failure(scenario, KeyRingFailureKind.ProviderCreation, "Provider creation failed: the configured factory could not create a provider."));
        }
        return operation.Value is null
            ? (null, Failure(scenario, KeyRingFailureKind.ProviderCreation, "Provider creation failed: the configured factory returned no provider."))
            : (operation.Value, null);
    }
}
