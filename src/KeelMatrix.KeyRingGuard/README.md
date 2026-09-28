# KeelMatrix.KeyRingGuard

Prove that an ASP.NET Core Data Protection payload survives restart, replica, and deterministic rotation transitions—and that intentionally isolated application or purpose boundaries reject it.

## Install

Install both packages:

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

## Supported runtime and platforms

The package targets `net8.0`. The repository's filesystem integration and package-consumer checks run on Windows, Linux, and macOS in the release-equivalent CI matrix. That evidence covers the shipped filesystem fixtures, not every third-party provider or deployment environment. The repository uses the .NET SDK `10.0.401`, pinned in `global.json`, to build the package.

## Safety and privacy

KeyRingGuard uses synthetic, ephemeral canaries only, never key material. It has no telemetry dependency and does not delete or revoke keys. A caller-supplied Data Protection provider can auto-generate keys during initialization and mutate a real shared store, even when the scenario is intended as a check. `KeyManagementOptions.AutoGenerateKeys` controls that provider behavior; use a temporary or dedicated test store by default. Only opt-in rotation explicitly requests one new key, and a callback returning a pre-existing key is rejected. Read the [security policy](https://github.com/KeelMatrix/KeyRingGuard/blob/main/SECURITY.md) and [privacy policy](https://github.com/KeelMatrix/KeyRingGuard/blob/main/PRIVACY.md).

## Compatibility

The durable public API is pre-1.0. Breaking changes are possible before the first public release, and the shipping API baseline is enforced by public API analyzers.

## Scenarios and evidence

The configured timeout applies to each provider, protector, protect, unprotect, key observation, and rotation operation. Queue admission canceled by that timeout is reported as `Timeout`; caller cancellation is reported as `Canceled`, and queued callbacks that miss admission do not run. Synchronous callbacks run through a bounded shared scheduler with 32 dedicated execution slots, so repeated hung callbacks cannot starve timeout observation or create an unbounded set of dedicated threads; an already-running synchronous callback cannot be forcibly interrupted and may finish in the background. KeyRingGuard keeps a provider alive until its scheduled callbacks finish, then disposes it; late provider results are disposed when they arrive. A nested verification started on a callback's own flowed execution context returns `InvalidScenario` before scheduler admission, and admission honors the caller's cancellation token. A callback that deliberately severs flow with `ExecutionContext.SuppressFlow`, `ThreadPool.UnsafeQueueUserWorkItem`, or a new thread is outside this guard and unsupported. Rotation additionally requires a cancellation-aware key-creation callback that returns a key absent from the opaque-key snapshot taken in the same bounded scheduled operation immediately before the callback, exactly one matching active non-revoked observed key, and a recreated provider that adopts that key. If the recreated provider does not return the standard ASP.NET Core Data Protection payload format, the result is `RotationUnavailable` because key adoption cannot be verified. Cancellation is cooperative for asynchronous callbacks.

Read the [scenario guide](https://github.com/KeelMatrix/KeyRingGuard/blob/main/docs/usage.md) and [verification boundary](https://github.com/KeelMatrix/KeyRingGuard/blob/main/docs/what-it-proves.md).
