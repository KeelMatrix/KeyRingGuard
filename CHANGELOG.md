# Changelog

This changelog records consumer-facing changes to KeelMatrix.KeyRingGuard.

## [Unreleased]

## [0.1.0] - 2026-09-26

### Added

- Provides bounded verification for restart continuity, replica sharing, application and purpose isolation, and opt-in rotation continuity using caller-supplied ASP.NET Core Data Protection providers.
- Requires independent provider instances for restart and replica transitions, establishes same-boundary controls before isolation verdicts, and observes an active key transition with both pre- and post-rotation canaries.
- Generates synthetic ephemeral canaries and sanitized diagnostics that exclude key material, key XML, provider configuration, and plaintext payloads.
- Targets `net8.0`, remains provider-neutral, and has no telemetry or provider SDK dependency.
- Documents Data Protection key auto-generation side effects and the tested filesystem/package-consumer evidence on Windows, Linux, and macOS.
