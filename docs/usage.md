# KeyRingGuard usage

## Install

Install both packages into the test project that owns the provider configuration:

```text
dotnet add package KeelMatrix.KeyRingGuard
dotnet add package Microsoft.AspNetCore.DataProtection.Extensions
```

The Extensions package provides `DataProtectionProvider.Create`, while KeyRingGuard keeps provider integrations consumer-owned.

## Quick Start

The package does not configure Data Protection for the application. Pass a factory that creates the same provider configuration the application uses, normally with a temporary or dedicated test store.

```csharp
using KeelMatrix.KeyRingGuard;
using Microsoft.AspNetCore.DataProtection;

var keyStorePath = Path.Combine(Path.GetTempPath(), "keyringguard-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(keyStorePath);

try
{
    var boundary = new KeyRingBoundary();

    KeyRingProviderFactory CreateFactory(string applicationName) =>
        new(
            _ => DataProtectionProvider.Create(
                new DirectoryInfo(keyStorePath),
                builder => builder.SetApplicationName(applicationName)),
            TimeSpan.FromSeconds(5),
            createKeyManager: null,
            boundary);

    var restartResult = await KeyRingVerifier.VerifyAsync(
        KeyRingScenario.RestartContinuity,
        CreateFactory("Sample.App"));
    RequireSuccess(restartResult);

    var isolationResult = await KeyRingVerifier.VerifyWithIsolationControlAsync(
        KeyRingScenario.ApplicationIsolation,
        CreateFactory("Sample.App"),
        CreateFactory("Other.App"),
        new KeyRingIsolationControl(
            CreateFactory("Sample.App"),
            CreateFactory("Sample.App")),
        CancellationToken.None);
    RequireSuccess(isolationResult);

    Console.WriteLine("KeyRingGuard quick start: PASS");
}
finally
{
    Directory.Delete(keyStorePath, recursive: true);
}

static void RequireSuccess(KeyRingVerificationResult result)
{
    if (!result.Succeeded)
    {
        throw new InvalidOperationException(result.Message);
    }
}
```

This quick start verifies both restart continuity and intentional application-name isolation against one temporary key store.

Factories are caller-owned. KeyRingGuard does not silently replace a failed or missing factory with an in-memory provider.

> Warning: Data Protection can auto-generate keys while a provider is initialized. A check against a real shared store may mutate that store even when the scenario itself only verifies continuity or isolation. `KeyManagementOptions.AutoGenerateKeys` controls this behavior for the caller's provider configuration. Use a temporary or dedicated test store by default.

## Restart continuity

`RestartContinuity` creates A, protects a private synthetic canary, waits for A's operations and owned notifications to quiesce, completes A's retirement, then creates B and checks the unchanged payload. A disposable provider must finish `Dispose` before B is admitted. One primary-factory timeout covers quiescence, scheduler admission, and teardown. Throwing retirement returns `ProviderCreation`; timeout or caller cancellation returns `Timeout` or `Canceled`. Each prevents B. A retirement attempt continues uncanceled after observation stops and is never repeated by final cleanup. Returning the same instance as B is rejected. Replica sharing retains simultaneous provider lifetimes.

## Replica sharing

`ReplicaSharing` accepts a second factory and checks both directions. Configure both factories with the intended shared store and the same application discriminator:

```csharp
var result = await KeyRingVerifier.VerifyAsync(
    KeyRingScenario.ReplicaSharing,
    replicaA,
    replicaB);
```

The scenario passes only when A can read B's payload and B can read A's payload.

## Application-name isolation

`ApplicationIsolation` accepts two factories configured with different application discriminators plus a same-boundary control. Because provider factories are opaque, declare one `KeyRingBoundary` instance on every factory that represents the same backing store. The control factories must use that same store and application discriminator and must exchange payloads in both directions before the isolated cross-provider checks run:

```csharp
var boundary = new KeyRingBoundary();
// Configure applicationA, applicationB, sharedApplicationA, and sharedApplicationB with boundary.
var result = await KeyRingVerifier.VerifyWithIsolationControlAsync(
    KeyRingScenario.ApplicationIsolation,
    applicationA,
    applicationB,
    new KeyRingIsolationControl(
        sharedApplicationA,
        sharedApplicationB),
    CancellationToken.None);
```

Each provider must first protect and unprotect its own payload, and an unexpected successful cross-unprotect is reported as `UnexpectedCrossUnprotect` and fails the result. A missing or mismatched boundary is an invalid scenario, so an unrelated third-store control cannot turn a storage mismatch into isolation evidence. After those controls succeed, a cross-check is accepted as isolation evidence only when the protector implements `IPersistedDataProtector` and reports a `CryptographicException`; an arbitrary provider exception is reported as `Unprotect` and fails closed. This standard-protector requirement keeps a provider-specific failure from becoming a false isolation pass.

## Purpose isolation

`PurposeIsolation` creates two independent providers from the supplied factory, first proves that they can exchange a same-purpose payload, then protects with two different purposes and expects cross-purpose unprotect to fail through the standard `IPersistedDataProtector` contract. An arbitrary provider exception is reported as `Unprotect` rather than accepted as proof. The purpose strings are owned by the verifier so the test remains synthetic and does not require a caller payload. A factory that silently selects a different store on each call fails the shared-boundary control.

## Rotation continuity

`RotationContinuity` is opt-in because it changes the supplied key store by asking the supplied `IKeyManager` to create one new key. After the initial provider and key-manager setup succeeds, the verifier snapshots the manager's opaque key IDs and invokes the callback in the same bounded scheduled operation; the callback must return a key ID that was absent from that snapshot. It then proves exactly one matching observed key is present, non-revoked, active, and adopted by a recreated independent provider, and checks both the old and new payloads. Resolve the provider and manager from the same provider setup for each factory call, and use one boundary identity to record that caller-declared linkage:

```csharp
var boundary = new KeyRingBoundary();
var factory = new KeyRingProviderFactory(
    _ => DataProtectionProvider.Create(new DirectoryInfo(keyStorePath), builder => builder.SetApplicationName("Orders.App")),
    TimeSpan.FromSeconds(5),
    _ => keyManager,
    (manager, activationDate, expirationDate, cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return manager.CreateNewKey(activationDate, expirationDate);
        }, cancellationToken),
    boundary);

var result = await KeyRingVerifier.VerifyAsync(
    KeyRingScenario.RotationContinuity,
    factory);
```

If no linked key-manager and key-creation factories are supplied, the result is `RotationUnavailable`. KeyRingGuard never deletes or revokes keys. A rotation callback that returns a pre-existing key is rejected as a failed rotation, even when that key is active. The recreated provider must return the canonical standard ASP.NET Core envelope: the `09 F0 C9 F0` header, the platform's little-endian `Guid` key ID, and a non-empty encryptor-specific payload. Canonical key-ID bytes remain supported when both byte orders produce identical bytes. Missing/truncated envelopes or another payload shape produce `RotationUnavailable` with a diagnostic explaining that key adoption cannot be verified. Every complete envelope is interpreted canonically. A wrong or stale key gives `RotationFailure`, including bytes equal to a distinct expected key's alternate representation; producer byte-order intent cannot be inferred from those bytes. A callback that ignores cancellation may continue after a timeout; callbacks must check cancellation before mutating the store and honor it during their work.

## Provider-factory patterns

For a non-`IDisposable` provider, retirement ends verifier use but cannot force garbage collection or destroy caller-held references. If reopening a store requires explicit release, return an `IDataProtectionProvider, IDisposable` adapter that owns and disposes the DI/service scope. The scenario recreates provider ownership in one process; it does not restart the application process.

`Timeout` and `Canceled` come only from the operation owner's sealed deadline/caller decision. An independent callback `OperationCanceledException` or canceled task is a provider, protect, unprotect, or rotation failure for that operation. A later caller cancellation during final cleanup can still return `Canceled`.

Each Protect/Unprotect call receives a working copy. The expected canaries and retained ciphertext stay private, and callback outputs are snapshotted before a later callback can reuse them. This supports ordinary wrappers that consume inputs or reuse output buffers. Concurrent mutation outside a callback's declared task lifetime remains unsupported.

The factory may be synchronous or asynchronous. Provider creation, protector creation, protect, unprotect, key observation, and rotation use their configured per-operation timeout. Queue-admission timeout returns `Timeout`, caller cancellation returns `Canceled`, and callbacks canceled before admission do not run. Use cancellation-aware asynchronous factories for network-backed stores.

Each configured timeout bounds observation, including queue admission. A timeout or cancellation signals the active operation token before returning, without waiting for consumer cancellation handlers. The 32 lifecycle reservations retain providers until operations, owned notifications, and best-effort cleanup finish. The 32 primary callback slots remain separate from bounded asynchronous cancellation dispatch. Final-cleanup timeout or exceptions preserve the verdict; caller cancellation during its bounded wait returns `Canceled`. Permanently blocked work can exhaust capacity. Duration excludes work continuing after the result. Flowed-context re-entry, including handlers and cleanup, returns `InvalidScenario`; deliberately severed flow is unsupported.

The authoritative timeout, cleanup, and ownership contract is the class XML documentation in [KeyRingVerifier.cs](../src/KeelMatrix.KeyRingGuard/KeyRingVerifier.cs). A factory transfers each returned provider to that verification for cleanup; supply independent provider instances and keep the backing store available until late work and cleanup finish. At most four distinct providers belong to one verification, so the 32 lifecycle reservations retain at most 128 providers and 32 cleanup batches. Providers within a cleanup batch are attempted sequentially; a blocked Dispose delays the remaining providers until it returns. A timed-out or canceled active operation adds no further cleanup wait. Cleanup cannot be canceled or dropped after ownership is accepted. No asynchronous disposal interface is invoked.

.NET cannot interrupt arbitrary user code that is already running. A permanently blocked operation, cancellation handler, or Dispose retains capacity until it returns; new calls can time out in lifecycle admission without creating a provider. This is a resource bound, not a guarantee that arbitrary callbacks finish. The re-entry guard includes cleanup and follows execution context; callbacks that deliberately use `ExecutionContext.SuppressFlow`, `ThreadPool.UnsafeQueueUserWorkItem`, or a new thread are outside the guard and unsupported.

Operation cancellation sources remain alive through observer decisions, operation completion, input-relay teardown, and notification completion. A signal means `IsCancellationRequested` is true; a blocked registration can delay other registrations, and registrations added after cancellation can run inline on their operation thread. Successful stages seal cancellation before progression; an accepted stop ends the scenario, with caller cancellation taking priority when observed at that decision. Operation and notification work must both finish before provider cleanup. No extra cleanup wait is added while either remains active. Detached work outside a callback's returned task is consumer-owned.

Provider exceptions are categorized without copying exception text into the result. Results distinguish provider creation, protect, unprotect, unexpected cross-unprotect, timeout, cancellation, and rotation failures.

## Filesystem test stores

Create a unique directory below the operating system temporary directory for each test, configure the provider to persist there, and delete it in a `finally`/`Dispose` path. Keep the store dedicated to the test. Do not point a continuity test at a developer profile or a shared production key ring.

Data Protection can auto-generate keys. A verification against a real shared store may therefore mutate that store even when the test only reads a payload. `KeyManagementOptions.AutoGenerateKeys` controls this behavior for the caller's provider configuration; use a temporary or dedicated store by default and make any deliberate shared-store mutation explicit in the test setup.

## Network-backed stores

KeyRingGuard itself performs no network calls and ships no Redis, Azure, AWS, or database SDK. A caller may supply a factory backed by one of those systems. Bound every provider operation with a finite timeout, honor cancellation, use test credentials through the host's secure configuration, and isolate the test data. A timeout or provider failure is returned as a bounded result; it is never replaced with an in-memory provider.

## Safety and privacy

The canary is random, bounded, ephemeral, and kept in memory. Result messages never include canaries, protected payloads, key XML, master material, key identifiers, store paths, application names, or raw provider exception text. KeyRingGuard has no telemetry dependency.
