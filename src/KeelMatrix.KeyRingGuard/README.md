# KeelMatrix.KeyRingGuard

Prove that an ASP.NET Core Data Protection payload survives restart, replica, and deterministic rotation transitionsвЂ”and that intentionally isolated application or purpose boundaries reject it.

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

Isolation succeeds only when the same-boundary control passes and each cross-check is rejected by the standard ASP.NET Core persisted-protector contract. An arbitrary provider `CryptographicException` is reported as an unprotect failure rather than accepted as isolation evidence.

## Supported runtime and platforms

The package targets `net8.0`. The repository's filesystem integration and package-consumer checks run on Windows, Linux, and macOS in the release-equivalent CI matrix. That evidence covers the shipped filesystem fixtures, not every third-party provider or deployment environment. The repository uses the .NET SDK `10.0.401`, pinned in `global.json`, to build the package.

## Safety and privacy

KeyRingGuard uses synthetic, ephemeral canaries only, never key material. It has no telemetry dependency and does not delete or revoke keys. A caller-supplied Data Protection provider can auto-generate keys during initialization and mutate a real shared store, even when the scenario is intended as a check. `KeyManagementOptions.AutoGenerateKeys` controls that provider behavior; use a temporary or dedicated test store by default. Only opt-in rotation explicitly requests one new key, and a callback returning a pre-existing key is rejected. Read the [security policy](https://github.com/KeelMatrix/KeyRingGuard/blob/main/SECURITY.md) and [privacy policy](https://github.com/KeelMatrix/KeyRingGuard/blob/main/PRIVACY.md).

## Compatibility

The public API follows a 0.x compatibility contract: minor releases may introduce breaking changes, documented in the changelog; patch releases preserve supported API compatibility. Public API analyzers enforce the shipping API baseline.

## Scenarios and evidence

Restart completes the first provider's retirement before recreation; failed or stopped retirement prevents the second factory call. Callback inputs and retained evidence use separate snapshots. Each configured timeout bounds observation, including queue admission. A timeout or cancellation signals the active operation token before returning, without waiting for consumer cancellation handlers. The 32 lifecycle reservations retain providers until operations, owned notifications, and best-effort cleanup finish. The 32 primary callback slots remain separate from bounded asynchronous cancellation dispatch. Final-cleanup timeout or exceptions preserve the verdict; caller cancellation during its bounded wait returns `Canceled`. Permanently blocked work can exhaust capacity. Duration excludes work continuing after the result. Flowed-context re-entry, including handlers and cleanup, returns `InvalidScenario`; deliberately severed flow is unsupported. See the [lifecycle guidance](https://github.com/KeelMatrix/KeyRingGuard/blob/main/docs/usage.md#provider-factory-patterns) and authoritative [API ownership contract](https://github.com/KeelMatrix/KeyRingGuard/blob/main/src/KeelMatrix.KeyRingGuard/KeyRingVerifier.cs).

Rotation additionally requires a cancellation-aware key-creation callback that returns a key absent from the opaque-key snapshot taken in the same bounded scheduled operation immediately before the callback, exactly one matching active non-revoked observed key, and a recreated provider that adopts that key. If the recreated provider does not return the standard ASP.NET Core Data Protection payload format, the result is `RotationUnavailable` because key adoption cannot be verified. Cancellation is cooperative for asynchronous callbacks.

Read the [scenario guide](https://github.com/KeelMatrix/KeyRingGuard/blob/main/docs/usage.md). The [verification boundary](https://github.com/KeelMatrix/KeyRingGuard/blob/main/docs/what-it-proves.md) defines canonical rotation envelope support, including key IDs whose two byte orders are identical.
