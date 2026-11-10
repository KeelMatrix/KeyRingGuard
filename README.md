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

The package targets `net8.0`. The repository's filesystem integration and package-consumer checks run on Windows, Linux, and macOS in the release-equivalent CI matrix. That evidence covers the shipped filesystem fixtures, not every third-party provider or deployment environment.

Building this repository requires the stable .NET SDK `10.0.401` or a later servicing patch in `10.0.4xx`. `global.json` prefers the exact baseline when installed and rejects other feature bands and preview SDKs. Visual Studio builds require Visual Studio 2026/MSBuild 18.0 or later; SDK `10.0.4xx` ships with Visual Studio 18.9. CI and release jobs install and require exactly `10.0.401`. See [SDK prerequisites and troubleshooting](https://github.com/KeelMatrix/KeyRingGuard/blob/main/docs/releasing.md#sdk-prerequisites). These source-build requirements do not change the package's `net8.0` consumer target.

## Safety and privacy

KeyRingGuard uses synthetic, ephemeral canaries only, never key material. It has no telemetry dependency and does not delete or revoke keys. A caller-supplied Data Protection provider can auto-generate keys during initialization and mutate a real shared store, even when the scenario is intended as a check. `KeyManagementOptions.AutoGenerateKeys` controls that provider behavior; use a temporary or dedicated test store by default. Only opt-in rotation explicitly requests one new key, and it accepts only one matching active, non-revoked observed key whose ID was absent from the opaque-key snapshot taken in the same bounded scheduled operation immediately before the rotation callback. Read [SECURITY.md](SECURITY.md) and [PRIVACY.md](PRIVACY.md).

Restart completes the first provider's retirement before recreation; failed or stopped retirement prevents the second factory call. Callback inputs and retained evidence use separate snapshots. Each configured timeout bounds observation, including queue admission. A timeout or cancellation signals the active operation token before returning, without waiting for consumer cancellation handlers. The 32 lifecycle reservations retain providers until operations, owned notifications, and best-effort cleanup finish. The 32 primary callback slots remain separate from bounded asynchronous cancellation dispatch. Final-cleanup timeout or exceptions preserve the verdict; caller cancellation during its bounded wait returns `Canceled`. Permanently blocked work can exhaust capacity. Duration excludes work continuing after the result. Flowed-context re-entry, including handlers and cleanup, returns `InvalidScenario`; deliberately severed flow is unsupported. See the [lifecycle guidance](https://github.com/KeelMatrix/KeyRingGuard/blob/main/docs/usage.md#provider-factory-patterns) and authoritative [API ownership contract](https://github.com/KeelMatrix/KeyRingGuard/blob/main/src/KeelMatrix.KeyRingGuard/KeyRingVerifier.cs).

## Compatibility

The public API follows a 0.x compatibility contract: minor releases may introduce breaking changes, documented in the changelog; patch releases preserve supported API compatibility. Public API analyzers enforce the shipping API baseline.

## Scenarios and evidence

See the [installation and scenario guide](docs/usage.md) for replica sharing, boundary-attributed isolation, linked rotation/new-key adoption, provider factories, cleanup, and safe network-backed testing. The [verification boundary](docs/what-it-proves.md) defines canonical rotation envelope support, including key IDs whose two byte orders are identical.

Repository maintainers: see [website discovery metadata and validation](docs/releasing.md#website-discovery-metadata) for the package classification contract.
