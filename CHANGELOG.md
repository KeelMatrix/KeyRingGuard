# Changelog

This changelog records consumer-facing changes to KeelMatrix.KeyRingGuard.

## [Unreleased]

## [0.1.0] - Planned (not yet published)

### Added

- Provides bounded verification for restart continuity, replica sharing, application and purpose isolation, and opt-in rotation continuity using caller-supplied ASP.NET Core Data Protection providers.
- Requires completed retirement before restart recreation, simultaneous independent replicas, explicit shared-store controls for application isolation, and canonical new-key adoption with old and new payload readability for opt-in rotation.
- Keeps expected canaries and retained evidence private while supporting provider callbacks that consume inputs or reuse output buffers.
- Bounds lifecycle admission and callback observation, distinguishes owned deadline/caller stops from independent callback failures, and retains providers through late operations, notifications, and exactly-once disposal attempts.
- Generates synthetic ephemeral canaries and sanitized diagnostics that exclude key material, key XML, provider configuration, and plaintext payloads.
- Targets `net8.0`, remains provider-neutral, and has no telemetry or provider SDK dependency.
- Documents Data Protection key auto-generation side effects and the tested filesystem/package-consumer evidence on Windows, Linux, and macOS.
