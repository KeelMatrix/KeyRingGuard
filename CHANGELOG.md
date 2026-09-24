# Changelog

This changelog records consumer-facing changes to KeelMatrix.KeyRingGuard.

## [Unreleased]

### Fixed

- Quick-start documentation now lists `Microsoft.AspNetCore.DataProtection.Extensions` for the `DataProtectionProvider.Create` example and validates the exact example through the package-consumer smoke.
- History hygiene accepts ordinary human attribution names while retaining automation and machine-signaled attribution checks.

### Added

- Bounded verification covers restart continuity, replica sharing, application and purpose isolation, and opt-in rotation continuity for caller-supplied Data Protection providers.
- Synthetic ephemeral canaries and sanitized diagnostics keep key material, key XML, provider configuration, and plaintext payloads out of results.
- The package targets `net8.0`, remains provider-neutral, and has no telemetry or provider SDK dependency.
