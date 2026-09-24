using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;

namespace KeelMatrix.KeyRingGuard;

/// <summary>
/// Executes provider-neutral Data Protection continuity and isolation scenarios.
/// </summary>
public static class KeyRingVerifier
{
    private const string SharedPurpose = "KeelMatrix.KeyRingGuard.Verification";
    private const string AlternatePurpose = "KeelMatrix.KeyRingGuard.Verification.Alternate";

    /// <summary>
    /// Verifies a scenario using one or two caller-supplied provider factories.
    /// </summary>
    /// <param name="scenario">The immutable scenario description to execute.</param>
    /// <param name="providerFactory">The primary provider factory.</param>
    /// <param name="secondaryProviderFactory">The second factory required by replica and application-isolation scenarios.</param>
    /// <param name="cancellationToken">Cancels provider creation and verification operations.</param>
    /// <returns>A structured result that never includes key material, canaries, or raw provider exceptions.</returns>
    public static async Task<KeyRingVerificationResult> VerifyAsync(
        KeyRingScenario scenario,
        KeyRingProviderFactory providerFactory,
        KeyRingProviderFactory? secondaryProviderFactory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(providerFactory);

        var stopwatch = Stopwatch.StartNew();
        var canary = RandomNumberGenerator.GetBytes(32);
        KeyRingVerificationResult result;

        if (scenario.IsRestartContinuity())
        {
            result = await VerifyContinuityAsync(scenario, providerFactory, canary, cancellationToken).ConfigureAwait(false);
        }
        else if (scenario.IsReplicaSharing())
        {
            result = await VerifyReplicaSharingAsync(scenario, providerFactory, secondaryProviderFactory, canary, cancellationToken).ConfigureAwait(false);
        }
        else if (scenario.IsApplicationIsolation())
        {
            result = await VerifyIsolationAsync(scenario, providerFactory, secondaryProviderFactory, canary, SharedPurpose, SharedPurpose, cancellationToken).ConfigureAwait(false);
        }
        else if (scenario.IsPurposeIsolation())
        {
            result = await VerifyIsolationAsync(scenario, providerFactory, providerFactory, canary, SharedPurpose, AlternatePurpose, cancellationToken).ConfigureAwait(false);
        }
        else if (scenario.IsRotationContinuity())
        {
            result = await VerifyRotationContinuityAsync(scenario, providerFactory, canary, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            result = Failure(scenario, KeyRingFailureKind.InvalidScenario, "The requested scenario is not supported.");
        }

        stopwatch.Stop();
        return new KeyRingVerificationResult(scenario, result.Succeeded, result.FailureKind, result.Message, stopwatch.Elapsed);
    }

    private static async Task<KeyRingVerificationResult> VerifyContinuityAsync(
        KeyRingScenario scenario,
        KeyRingProviderFactory providerFactory,
        byte[] canary,
        CancellationToken cancellationToken)
    {
        var first = await CreateProviderAsync(scenario, providerFactory, cancellationToken).ConfigureAwait(false);
        if (first.Failure is not null)
        {
            return first.Failure;
        }

        var protectorResult = await CreateProtectorAsync(
            scenario,
            first.Provider!,
            SharedPurpose,
            providerFactory.Timeout,
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
            cancellationToken).ConfigureAwait(false);
        if (protectedResult.Failure is not null)
        {
            return protectedResult.Failure;
        }

        var second = await CreateProviderAsync(scenario, providerFactory, cancellationToken).ConfigureAwait(false);
        if (second.Failure is not null)
        {
            return second.Failure;
        }

        return await UnprotectAndCompareAsync(
            scenario,
            second.Provider!,
            protectedResult.Value!,
            canary,
            SharedPurpose,
            providerFactory.Timeout,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<KeyRingVerificationResult> VerifyReplicaSharingAsync(
        KeyRingScenario scenario,
        KeyRingProviderFactory providerFactory,
        KeyRingProviderFactory? secondaryProviderFactory,
        byte[] canary,
        CancellationToken cancellationToken)
    {
        if (secondaryProviderFactory is null)
        {
            return Failure(scenario, KeyRingFailureKind.InvalidScenario, "Replica sharing requires a second provider factory.");
        }

        var first = await CreateProviderAsync(scenario, providerFactory, cancellationToken).ConfigureAwait(false);
        if (first.Failure is not null)
        {
            return first.Failure;
        }

        var second = await CreateProviderAsync(scenario, secondaryProviderFactory, cancellationToken).ConfigureAwait(false);
        if (second.Failure is not null)
        {
            return second.Failure;
        }

        var firstProtectorResult = await CreateProtectorAsync(
            scenario,
            first.Provider!,
            SharedPurpose,
            providerFactory.Timeout,
            cancellationToken).ConfigureAwait(false);
        if (firstProtectorResult.Failure is not null)
        {
            return firstProtectorResult.Failure;
        }

        var secondProtectorResult = await CreateProtectorAsync(
            scenario,
            second.Provider!,
            SharedPurpose,
            secondaryProviderFactory.Timeout,
            cancellationToken).ConfigureAwait(false);
        if (secondProtectorResult.Failure is not null)
        {
            return secondProtectorResult.Failure;
        }

        var firstProtectedResult = await ProtectAsync(
            scenario,
            firstProtectorResult.Value!,
            canary,
            providerFactory.Timeout,
            cancellationToken).ConfigureAwait(false);
        if (firstProtectedResult.Failure is not null)
        {
            return firstProtectedResult.Failure;
        }

        var secondProtectedResult = await ProtectAsync(
            scenario,
            secondProtectorResult.Value!,
            canary,
            secondaryProviderFactory.Timeout,
            cancellationToken).ConfigureAwait(false);
        if (secondProtectedResult.Failure is not null)
        {
            return secondProtectedResult.Failure;
        }

        var firstResult = await UnprotectAndCompareAsync(
            scenario,
            second.Provider!,
            firstProtectedResult.Value!,
            canary,
            SharedPurpose,
            secondaryProviderFactory.Timeout,
            cancellationToken).ConfigureAwait(false);
        if (!firstResult.Succeeded)
        {
            return firstResult;
        }

        return await UnprotectAndCompareAsync(
            scenario,
            first.Provider!,
            secondProtectedResult.Value!,
            canary,
            SharedPurpose,
            providerFactory.Timeout,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<KeyRingVerificationResult> VerifyIsolationAsync(
        KeyRingScenario scenario,
        KeyRingProviderFactory providerFactory,
        KeyRingProviderFactory? secondaryProviderFactory,
        byte[] canary,
        string firstPurpose,
        string secondPurpose,
        CancellationToken cancellationToken)
    {
        if (secondaryProviderFactory is null)
        {
            return Failure(scenario, KeyRingFailureKind.InvalidScenario, "Isolation requires a second provider factory.");
        }

        var first = await CreateProviderAsync(scenario, providerFactory, cancellationToken).ConfigureAwait(false);
        if (first.Failure is not null)
        {
            return first.Failure;
        }

        var second = await CreateProviderAsync(scenario, secondaryProviderFactory, cancellationToken).ConfigureAwait(false);
        if (second.Failure is not null)
        {
            return second.Failure;
        }

        var firstProtectorResult = await CreateProtectorAsync(
            scenario,
            first.Provider!,
            firstPurpose,
            providerFactory.Timeout,
            cancellationToken).ConfigureAwait(false);
        if (firstProtectorResult.Failure is not null)
        {
            return firstProtectorResult.Failure;
        }

        var secondProtectorResult = await CreateProtectorAsync(
            scenario,
            second.Provider!,
            secondPurpose,
            secondaryProviderFactory.Timeout,
            cancellationToken).ConfigureAwait(false);
        if (secondProtectorResult.Failure is not null)
        {
            return secondProtectorResult.Failure;
        }

        var firstProtectedResult = await ProtectAsync(
            scenario,
            firstProtectorResult.Value!,
            canary,
            providerFactory.Timeout,
            cancellationToken).ConfigureAwait(false);
        if (firstProtectedResult.Failure is not null)
        {
            return firstProtectedResult.Failure;
        }

        var secondProtectedResult = await ProtectAsync(
            scenario,
            secondProtectorResult.Value!,
            canary,
            secondaryProviderFactory.Timeout,
            cancellationToken).ConfigureAwait(false);
        if (secondProtectedResult.Failure is not null)
        {
            return secondProtectedResult.Failure;
        }

        var firstCrossResult = await ExpectUnprotectFailureAsync(
            scenario,
            second.Provider!,
            firstProtectedResult.Value!,
            secondPurpose,
            secondaryProviderFactory.Timeout,
            cancellationToken).ConfigureAwait(false);
        if (!firstCrossResult.Succeeded)
        {
            return firstCrossResult;
        }

        return await ExpectUnprotectFailureAsync(
            scenario,
            first.Provider!,
            secondProtectedResult.Value!,
            firstPurpose,
            providerFactory.Timeout,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<KeyRingVerificationResult> VerifyRotationContinuityAsync(
        KeyRingScenario scenario,
        KeyRingProviderFactory providerFactory,
        byte[] canary,
        CancellationToken cancellationToken)
    {
        if (!providerFactory.HasKeyManagerFactory)
        {
            return Failure(scenario, KeyRingFailureKind.RotationUnavailable, "Rotation continuity requires an explicitly supplied key-manager factory.");
        }

        var first = await CreateProviderAsync(scenario, providerFactory, cancellationToken).ConfigureAwait(false);
        if (first.Failure is not null)
        {
            return first.Failure;
        }

        var protectorResult = await CreateProtectorAsync(
            scenario,
            first.Provider!,
            SharedPurpose,
            providerFactory.Timeout,
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
            cancellationToken).ConfigureAwait(false);
        if (protectedResult.Failure is not null)
        {
            return protectedResult.Failure;
        }

        var manager = await CreateKeyManagerAsync(scenario, providerFactory, cancellationToken).ConfigureAwait(false);
        if (manager.Failure is not null)
        {
            return manager.Failure;
        }

        var rotationResult = await ExecuteBoundedAsync(
            () => manager.Manager!.CreateNewKey(DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow.AddDays(1)),
            providerFactory.Timeout,
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

        var second = await CreateProviderAsync(scenario, providerFactory, cancellationToken).ConfigureAwait(false);
        if (second.Failure is not null)
        {
            return second.Failure;
        }

        return await UnprotectAndCompareAsync(
            scenario,
            second.Provider!,
            protectedResult.Value!,
            canary,
            SharedPurpose,
            providerFactory.Timeout,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(IDataProtector? Value, KeyRingVerificationResult? Failure)> CreateProtectorAsync(
        KeyRingScenario scenario,
        IDataProtectionProvider provider,
        string purpose,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var operation = await ExecuteBoundedAsync(
            () => provider.CreateProtector(purpose),
            timeout,
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
        CancellationToken cancellationToken)
    {
        var operation = await ExecuteBoundedAsync(
            () => protector.Protect(canary),
            timeout,
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

    private static async Task<KeyRingVerificationResult> UnprotectAndCompareAsync(
        KeyRingScenario scenario,
        IDataProtectionProvider provider,
        byte[] protectedPayload,
        byte[] canary,
        string purpose,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var protectorResult = await CreateProtectorAsync(
            scenario,
            provider,
            purpose,
            timeout,
            cancellationToken).ConfigureAwait(false);
        if (protectorResult.Failure is not null)
        {
            return protectorResult.Failure.FailureKind == KeyRingFailureKind.Protect
                ? Failure(scenario, KeyRingFailureKind.Unprotect, "Unprotect failed: the configured provider could not create a protector.")
                : protectorResult.Failure;
        }

        var operation = await ExecuteBoundedAsync(
            () => protectorResult.Value!.Unprotect(protectedPayload),
            timeout,
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
        CancellationToken cancellationToken)
    {
        var protectorResult = await CreateProtectorAsync(
            scenario,
            provider,
            purpose,
            timeout,
            cancellationToken).ConfigureAwait(false);
        if (protectorResult.Failure is not null)
        {
            return protectorResult.Failure.FailureKind == KeyRingFailureKind.Protect
                ? Failure(scenario, KeyRingFailureKind.Unprotect, "Unprotect failed: the configured provider could not create a protector.")
                : protectorResult.Failure;
        }

        var operation = await ExecuteBoundedAsync(
            () => protectorResult.Value!.Unprotect(protectedPayload),
            timeout,
            cancellationToken).ConfigureAwait(false);
        if (operation.TimedOut)
        {
            return Failure(scenario, KeyRingFailureKind.Timeout, "Isolation check timed out before the configured bound.");
        }
        if (operation.Canceled)
        {
            return Failure(scenario, KeyRingFailureKind.Canceled, "Verification was canceled.");
        }
        if (operation.Exception is CryptographicException)
        {
            return Success(scenario, "Isolation passed.");
        }
        if (operation.Exception is not null)
        {
            return Failure(scenario, KeyRingFailureKind.Unprotect, "Isolation failed: the configured provider could not complete the cross-boundary check.");
        }

        return Failure(scenario, KeyRingFailureKind.UnexpectedCrossUnprotect, "Isolation failed: a provider from an isolated boundary successfully unprotected a payload.");
    }

    private static async Task<BoundedOperation<T>> ExecuteBoundedAsync<T>(
        Func<T> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Task<T> task;
        try
        {
            task = Task.Run(operation);
            ObserveFaults(task);
        }
        catch (Exception exception)
        {
            return new(default, exception, false, false);
        }

        try
        {
            return new(await task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false), null, false, false);
        }
        catch (TimeoutException)
        {
            return new(default, null, true, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(default, null, false, true);
        }
        catch (Exception exception)
        {
            return new(default, exception, false, false);
        }
    }

    private static void ObserveFaults<T>(Task<T> task) =>
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private readonly record struct BoundedOperation<T>(T? Value, Exception? Exception, bool TimedOut, bool Canceled);

    private static async Task<(IDataProtectionProvider? Provider, KeyRingVerificationResult? Failure)> CreateProviderAsync(
        KeyRingScenario scenario,
        KeyRingProviderFactory factory,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(factory.Timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        Task<IDataProtectionProvider> task;

        try
        {
            task = factory.CreateProviderAsync(linkedSource.Token);
            if (task is null)
            {
                return (null, Failure(scenario, KeyRingFailureKind.ProviderCreation, "Provider creation failed: the factory returned no operation."));
            }
            ObserveFaults(task);
        }
        catch (Exception)
        {
            return (null, Failure(scenario, KeyRingFailureKind.ProviderCreation, "Provider creation failed: the configured factory could not create a provider."));
        }

        try
        {
            var provider = await task.WaitAsync(factory.Timeout, cancellationToken).ConfigureAwait(false);
            return provider is null
                ? (null, Failure(scenario, KeyRingFailureKind.ProviderCreation, "Provider creation failed: the configured factory returned no provider."))
                : (provider, null);
        }
        catch (TimeoutException)
        {
            linkedSource.Cancel();
            return (null, Failure(scenario, KeyRingFailureKind.Timeout, "Provider creation timed out before the configured bound."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (null, Failure(scenario, KeyRingFailureKind.Canceled, "Verification was canceled."));
        }
        catch (OperationCanceledException)
        {
            return (null, Failure(scenario, KeyRingFailureKind.Timeout, "Provider creation timed out before the configured bound."));
        }
        catch (Exception)
        {
            return (null, Failure(scenario, KeyRingFailureKind.ProviderCreation, "Provider creation failed: the configured factory could not create a provider."));
        }
    }

    private static async Task<(IKeyManager? Manager, KeyRingVerificationResult? Failure)> CreateKeyManagerAsync(
        KeyRingScenario scenario,
        KeyRingProviderFactory factory,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(factory.Timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        Task<IKeyManager> task;

        try
        {
            task = factory.CreateKeyManagerAsync(linkedSource.Token);
            ObserveFaults(task);
        }
        catch (Exception)
        {
            return (null, Failure(scenario, KeyRingFailureKind.RotationFailure, "Rotation failed: the configured key-manager factory could not create a manager."));
        }

        try
        {
            var manager = await task.WaitAsync(factory.Timeout, cancellationToken).ConfigureAwait(false);
            return manager is null
                ? (null, Failure(scenario, KeyRingFailureKind.RotationFailure, "Rotation failed: the configured key-manager factory returned no manager."))
                : (manager, null);
        }
        catch (TimeoutException)
        {
            linkedSource.Cancel();
            return (null, Failure(scenario, KeyRingFailureKind.Timeout, "Key-manager creation timed out before the configured bound."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (null, Failure(scenario, KeyRingFailureKind.Canceled, "Verification was canceled."));
        }
        catch (OperationCanceledException)
        {
            return (null, Failure(scenario, KeyRingFailureKind.Timeout, "Key-manager creation timed out before the configured bound."));
        }
        catch (Exception)
        {
            return (null, Failure(scenario, KeyRingFailureKind.RotationFailure, "Rotation failed: the configured key-manager factory could not create a manager."));
        }
    }

    private static KeyRingVerificationResult Success(KeyRingScenario scenario, string message) =>
        new(scenario, true, KeyRingFailureKind.None, message, TimeSpan.Zero);

    private static KeyRingVerificationResult Failure(KeyRingScenario scenario, KeyRingFailureKind kind, string message) =>
        new(scenario, false, kind, message, TimeSpan.Zero);
}

internal static class KeyRingScenarioExtensions
{
    internal static bool IsRestartContinuity(this KeyRingScenario scenario) => scenario == KeyRingScenario.RestartContinuity;

    internal static bool IsReplicaSharing(this KeyRingScenario scenario) => scenario == KeyRingScenario.ReplicaSharing;

    internal static bool IsApplicationIsolation(this KeyRingScenario scenario) => scenario == KeyRingScenario.ApplicationIsolation;

    internal static bool IsPurposeIsolation(this KeyRingScenario scenario) => scenario == KeyRingScenario.PurposeIsolation;

    internal static bool IsRotationContinuity(this KeyRingScenario scenario) => scenario == KeyRingScenario.RotationContinuity;
}
