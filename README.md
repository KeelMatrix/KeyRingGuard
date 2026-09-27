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
    var factory = new KeyRingProviderFactory(
        _ => DataProtectionProvider.Create(
            new DirectoryInfo(keyStorePath),
            builder => builder.SetApplicationName("Sample.App")),
        TimeSpan.FromSeconds(5));

    var result = await KeyRingVerifier.VerifyAsync(
        KeyRingScenario.RestartContinuity,
        factory);

    if (!result.Succeeded)
    {
        throw new InvalidOperationException(result.Message);
    }

    Console.WriteLine("KeyRingGuard quick start: PASS");
}
finally
{
    Directory.Delete(keyStorePath, recursive: true);
}
```

## Supported runtime and platforms

The package targets `net8.0`. The repository's filesystem integration and package-consumer checks run on Windows, Linux, and macOS in the release-equivalent CI matrix. That evidence covers the shipped filesystem fixtures, not every third-party provider or deployment environment. The repository uses the .NET SDK `10.0.401`, pinned in `global.json`, to build the package.

## Safety and privacy

KeyRingGuard uses synthetic, ephemeral canaries only, never key material. It has no telemetry dependency and does not delete or revoke keys. A caller-supplied Data Protection provider can auto-generate keys during initialization and mutate a real shared store, even when the scenario is intended as a check. `KeyManagementOptions.AutoGenerateKeys` controls that provider behavior; use a temporary or dedicated test store by default. Only opt-in rotation explicitly requests one new key, and it accepts only one matching active, non-revoked observed key whose ID was absent before the rotation callback. Read [SECURITY.md](SECURITY.md) and [PRIVACY.md](PRIVACY.md).

Each configured timeout bounds the verifier's wait. Queue admission canceled by that timeout is reported as `Timeout`; caller cancellation is reported as `Canceled`, and queued callbacks that miss admission do not run. A synchronous callback that has already started may finish in the background; KeyRingGuard keeps its provider alive until that callback completes, then disposes it. Asynchronous provider, manager, and rotation callbacks receive the same cancellation and timeout boundary. See the [timeout and callback guidance](docs/usage.md#provider-factory-patterns).

## Compatibility

The durable public API is pre-1.0. Breaking changes are possible before the first public release, and the shipping API baseline is enforced by public API analyzers.

## Scenarios and evidence

See the [installation and scenario guide](docs/usage.md) for replica sharing, boundary-attributed isolation, linked rotation/new-key adoption, provider factories, cleanup, and safe network-backed testing. See [what it proves](docs/what-it-proves.md) for the verification boundary.
