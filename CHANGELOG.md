# Changelog

This changelog records consumer-facing changes to KeelMatrix.KeyRingGuard.

## [Unreleased]

## [0.1.0] - 2026-11-08

### Added

- Verifies restart continuity, bidirectional replica sharing, application and purpose isolation, and opt-in key-rotation continuity using caller-supplied ASP.NET Core Data Protection providers.
- Reports structured outcomes for provider and payload failures, isolation mismatches, rotation availability, timeouts, and caller cancellation.
- Bounds callback observation and retains provider ownership until active operations, cancellation notifications, and best-effort cleanup finish; arbitrary running callbacks cannot be forcibly interrupted.
- Uses private synthetic canaries and sanitized diagnostics without reading or emitting key XML; provider initialization and opt-in rotation can mutate caller-owned stores.
- Targets `net8.0` with consumer-owned provider integrations and no telemetry or provider SDK dependency.
