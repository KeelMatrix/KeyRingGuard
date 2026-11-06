# KeelMatrix.KeyRingGuard

`KeelMatrix.KeyRingGuard` verifies the Data Protection transitions an application depends on: restart continuity, replica sharing, intentional application and purpose isolation, and deterministic key-rotation continuity.

## Install

Install both packages in a test project:

```text
dotnet add package KeelMatrix.KeyRingGuard
dotnet add package Microsoft.AspNetCore.DataProtection.Extensions
```

The Extensions package provides `DataProtectionProvider.Create`, while KeyRingGuard keeps provider integrations consumer-owned.

## Quick Start

First success:

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

KeyRingGuard uses synthetic, ephemeral canaries only, never key material. It has no telemetry dependency and does not delete or revoke keys. A caller-supplied Data Protection provider can auto-generate keys during initialization and mutate a real shared store, even when the scenario is intended as a check. `KeyManagementOptions.AutoGenerateKeys` controls that provider behavior; use a temporary or dedicated test store by default. Only opt-in rotation explicitly requests one new key, and it accepts only one matching active, non-revoked observed key whose ID was absent from the opaque-key snapshot taken in the same bounded scheduled operation immediately before the rotation callback. Read [SECURITY.md](SECURITY.md) and [PRIVACY.md](PRIVACY.md).

Each configured timeout bounds observation of an operation, including queue admission; caller cancellation returns `Canceled`. At most 32 verification lifecycles retain providers through active work and cleanup, and all provider, key-manager, rotation, and cleanup callbacks share 32 execution slots. Cleanup waits until active operations finish and attempts each distinct disposable provider once. The caller waits at most the primary factory timeout for best-effort cleanup: a cleanup timeout or exception preserves the scenario verdict, while cancellation during that wait returns `Canceled`. Cleanup continues with its lifecycle reservation held; permanently blocked user code can exhaust capacity, but repeated calls cannot accumulate unbounded cleanup work or dedicated threads. Duration includes admission and cleanup actually awaited, excluding work continuing afterward. Callback re-entry on flowed execution context returns `InvalidScenario`; deliberately severed flow remains unsupported. See the [lifecycle guidance](https://github.com/KeelMatrix/KeyRingGuard/blob/main/docs/usage.md#provider-factory-patterns) and authoritative [API ownership contract](https://github.com/KeelMatrix/KeyRingGuard/blob/main/src/KeelMatrix.KeyRingGuard/KeyRingVerifier.cs).

## Compatibility

The public API follows a 0.x compatibility contract: minor releases may introduce breaking changes, documented in the changelog; patch releases preserve supported API compatibility. Public API analyzers enforce the shipping API baseline.

## Scenarios and evidence

See the [installation and scenario guide](docs/usage.md) for replica sharing, boundary-attributed isolation, linked rotation/new-key adoption, provider factories, cleanup, and safe network-backed testing. See [what it proves](docs/what-it-proves.md) for the verification boundary.
