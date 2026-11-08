using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection.KeyManagement;

namespace KeelMatrix.KeyRingGuard;

public static partial class KeyRingVerifier
{
    private static async Task<KeyRingVerificationResult> VerifyRotationContinuityAsync(
        KeyRingScenario scenario,
        KeyRingProviderFactory providerFactory,
        byte[] canary,
        ProviderScope providerScope,
        CancellationToken cancellationToken)
    {
        if (!providerFactory.HasKeyManagerFactory || !providerFactory.HasNewKeyFactory)
        {
            return Failure(scenario, KeyRingFailureKind.RotationUnavailable, "Rotation continuity requires explicitly supplied key-manager and cancellation-aware key-creation factories.");
        }

        var first = await CreateProviderAsync(scenario, providerFactory, providerScope, cancellationToken).ConfigureAwait(false);
        if (first.Failure is not null)
        {
            return first.Failure;
        }

        var protectorResult = await CreateProtectorAsync(
            scenario,
            first.Provider!,
            SharedPurpose,
            providerFactory.Timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (protectorResult.Failure is not null)
        {
            return protectorResult.Failure;
        }

        var protectedResult = await ProtectAsync(
            scenario,
            protectorResult.Value!,
            canary,
            providerFactory.Timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (protectedResult.Failure is not null)
        {
            return protectedResult.Failure;
        }

        var manager = await CreateKeyManagerAsync(scenario, providerFactory, providerScope, cancellationToken).ConfigureAwait(false);
        if (manager.Failure is not null)
        {
            return manager.Failure;
        }

        var rotationStart = DateTimeOffset.UtcNow;
        var activationDate = rotationStart.AddSeconds(-1);
        var expirationDate = rotationStart.AddDays(1);
        var rotationResult = await ExecuteOwnedAsync(
            token => KeyRingOperationScheduler.RunAsync(
                () => providerFactory.CreateNewKeyWithSnapshotAsync(
                    manager.Manager!, activationDate, expirationDate, SnapshotKeyIds, token), token),
            providerFactory.Timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (rotationResult.TimedOut)
        {
            return Failure(scenario, KeyRingFailureKind.Timeout, "Rotation timed out before the configured bound.");
        }
        if (rotationResult.Canceled)
        {
            return Failure(scenario, KeyRingFailureKind.Canceled, "Verification was canceled.");
        }
        if (rotationResult.Exception is not null)
        {
            return Failure(scenario, KeyRingFailureKind.RotationFailure, "Rotation failed: the supplied key manager could not create a new key.");
        }

        if (rotationResult.Value.ExistingKeyIds is null)
        {
            return Failure(scenario, KeyRingFailureKind.RotationFailure, "Rotation failed: the supplied key manager could not expose its existing keys.");
        }

        if (rotationResult.Value.NewKey is null)
        {
            return Failure(scenario, KeyRingFailureKind.RotationFailure, "Rotation failed: the supplied key manager did not return a new key.");
        }

        var existingKeyIds = rotationResult.Value.ExistingKeyIds;
        var newKey = rotationResult.Value.NewKey;
        var observedKey = await ExecuteBoundedAsync(
            () => ObserveNewActiveKey(manager.Manager!, newKey, existingKeyIds, DateTimeOffset.UtcNow),
            providerFactory.Timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (observedKey.TimedOut)
        {
            return Failure(scenario, KeyRingFailureKind.Timeout, "Rotation key observation timed out before the configured bound.");
        }
        if (observedKey.Canceled)
        {
            return Failure(scenario, KeyRingFailureKind.Canceled, "Verification was canceled.");
        }
        if (observedKey.Exception is not null)
        {
            return Failure(scenario, KeyRingFailureKind.RotationFailure, "Rotation failed: the supplied key manager could not expose the new key.");
        }

        if (!observedKey.Value.Value)
        {
            return Failure(scenario, KeyRingFailureKind.RotationFailure, "Rotation failed: the new key was not observed as active.");
        }

        var second = await CreateProviderAsync(scenario, providerFactory, providerScope, cancellationToken).ConfigureAwait(false);
        if (second.Failure is not null)
        {
            return second.Failure;
        }

        if (ReferenceEquals(first.Provider, second.Provider))
        {
            return Failure(
                scenario,
                KeyRingFailureKind.ProviderCreation,
                "Provider creation failed: the rotation factory reused the same provider instance.");
        }

        var postRotationCanary = RandomNumberGenerator.GetBytes(32);
        var postRotationProtector = await CreateProtectorAsync(
            scenario,
            second.Provider!,
            SharedPurpose,
            providerFactory.Timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (postRotationProtector.Failure is not null)
        {
            return postRotationProtector.Failure;
        }

        var postRotationProtected = await ProtectAsync(
            scenario,
            postRotationProtector.Value!,
            postRotationCanary,
            providerFactory.Timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (postRotationProtected.Failure is not null)
        {
            return postRotationProtected.Failure;
        }

        var payloadFormat = InspectPayloadFormat(postRotationProtected.Value!, observedKey.Value.KeyId);
        if (payloadFormat == RotationPayloadFormat.Unsupported)
        {
            return Failure(
                scenario,
                KeyRingFailureKind.RotationUnavailable,
                "Rotation unavailable: the recreated provider returned a payload outside the standard ASP.NET Core Data Protection payload format, so key adoption could not be verified.");
        }

        if (payloadFormat == RotationPayloadFormat.StandardButDifferentKey)
        {
            return Failure(scenario, KeyRingFailureKind.RotationFailure, "Rotation failed: the recreated provider did not adopt the observed active key.");
        }

        var oldPayloadResult = await UnprotectAndCompareAsync(
            scenario,
            second.Provider!,
            protectedResult.Value!,
            canary,
            SharedPurpose,
            providerFactory.Timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
        if (!oldPayloadResult.Succeeded)
        {
            return oldPayloadResult;
        }

        return await UnprotectAndCompareAsync(
            scenario,
            second.Provider!,
            postRotationProtected.Value!,
            postRotationCanary,
            SharedPurpose,
            providerFactory.Timeout,
            providerScope,
            cancellationToken).ConfigureAwait(false);
    }

    private static HashSet<Guid> SnapshotKeyIds(IKeyManager manager)
    {
        var keyIds = new HashSet<Guid>();
        foreach (var key in manager.GetAllKeys())
        {
            if (key is not null)
            {
                keyIds.Add(key.KeyId);
            }
        }

        return keyIds;
    }

    private static RotationObservation ObserveNewActiveKey(
        IKeyManager manager,
        IKey newKey,
        HashSet<Guid> existingKeyIds,
        DateTimeOffset now)
    {
        var keyId = newKey.KeyId;
        if (existingKeyIds.Contains(keyId))
        {
            return new RotationObservation(keyId, false);
        }

        IKey? observedKey = null;
        foreach (var key in manager.GetAllKeys())
        {
            if (key is not null && key.KeyId == keyId)
            {
                if (observedKey is not null)
                {
                    return new RotationObservation(keyId, false);
                }

                observedKey = key;
            }
        }

        var active = observedKey is not null
            && !observedKey.IsRevoked
            && observedKey.ActivationDate <= now
            && now < observedKey.ExpirationDate;
        return new RotationObservation(keyId, active);
    }

    private static RotationPayloadFormat InspectPayloadFormat(byte[] protectedPayload, Guid keyId)
    {
        if (protectedPayload.Length <= 20
            || protectedPayload[0] != 0x09
            || protectedPayload[1] != 0xF0
            || protectedPayload[2] != 0xC9
            || protectedPayload[3] != 0xF0)
        {
            return RotationPayloadFormat.Unsupported;
        }

        var payloadKey = protectedPayload.AsSpan(4, 16);
        Span<byte> littleEndianKey = stackalloc byte[16];
        keyId.TryWriteBytes(littleEndianKey);
        // Canonical bytes can also match the alternate representation for symmetric IDs.
        if (payloadKey.SequenceEqual(littleEndianKey))
        {
            return RotationPayloadFormat.StandardWithExpectedKey;
        }

        // Every complete envelope names a canonical ID. Alternate-byte equality
        // can also name a distinct stale key and cannot establish producer intent.
        return RotationPayloadFormat.StandardButDifferentKey;
    }

    private enum RotationPayloadFormat
    {
        Unsupported,
        StandardButDifferentKey,
        StandardWithExpectedKey,
    }

    private readonly record struct RotationObservation(Guid KeyId, bool Value);

    private static async Task<(IKeyManager? Manager, KeyRingVerificationResult? Failure)> CreateKeyManagerAsync(
        KeyRingScenario scenario,
        KeyRingProviderFactory factory,
        ProviderScope providerScope,
        CancellationToken cancellationToken)
    {
        var operation = await ExecuteOwnedAsync(factory.CreateKeyManagerAsync, factory.Timeout, providerScope, cancellationToken).ConfigureAwait(false);
        if (operation.Canceled)
        {
            return (null, Failure(scenario, KeyRingFailureKind.Canceled, "Verification was canceled."));
        }
        if (operation.TimedOut)
        {
            return (null, Failure(scenario, KeyRingFailureKind.Timeout, "Key-manager creation timed out before the configured bound."));
        }
        if (operation.Exception is not null)
        {
            return (null, Failure(scenario, KeyRingFailureKind.RotationFailure, "Rotation failed: the configured key-manager factory could not create a manager."));
        }
        return operation.Value is null
            ? (null, Failure(scenario, KeyRingFailureKind.RotationFailure, "Rotation failed: the configured key-manager factory returned no manager."))
            : (operation.Value, null);
    }
}
