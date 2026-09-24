# KeelMatrix.KeyRingGuard

`KeelMatrix.KeyRingGuard` verifies the Data Protection transitions an application depends on: restart continuity, replica sharing, intentional application and purpose isolation, and deterministic key-rotation continuity.

## Install

Install it in a test project:

```text
dotnet add package KeelMatrix.KeyRingGuard
```

## Quick Start

First success:

```csharp
var factory = new KeyRingProviderFactory(
    _ => DataProtectionProvider.Create(
        keyStorePath,
        builder => builder.SetApplicationName("Sample.App")),
    TimeSpan.FromSeconds(5));

var result = await KeyRingVerifier.VerifyAsync(
    KeyRingScenario.RestartContinuity,
    factory);

Assert.True(result.Succeeded, result.Message);
```

## Supported runtime and platforms

The package targets `net8.0`. Windows is the locally evidenced platform for this candidate; Linux and macOS are not yet evidenced here. The repository and release workflow use the .NET SDK `10.0.401` to build the `net8.0` package, pinned in `global.json` and installed by the release workflow.

## Safety and privacy

KeyRingGuard uses synthetic, ephemeral canaries only, never key material. It has no telemetry dependency and does not delete or revoke keys. Verification is non-destructive to existing keys; opt-in rotation creates one new key in the caller's dedicated test store. Read [SECURITY.md](SECURITY.md) and [PRIVACY.md](PRIVACY.md).

## Compatibility

The durable public API is pre-1.0. Breaking changes are possible before the first public release, and the shipping API baseline is enforced by public API analyzers.

## Scenarios and evidence

See the [installation and scenario guide](docs/usage.md) for replica sharing, isolation, rotation, provider factories, cleanup, and safe network-backed testing. See [what it proves](docs/what-it-proves.md) for the verification boundary.
