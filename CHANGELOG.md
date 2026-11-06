# Changelog

This changelog records consumer-facing changes to KeelMatrix.KeyRingGuard.

## [Unreleased]

## [0.1.0] - 2026-09-26

### Added

- Provides bounded verification for restart continuity, replica sharing, application and purpose isolation, and opt-in rotation continuity using caller-supplied ASP.NET Core Data Protection providers.
- Requires independent provider instances for restart and replica transitions, explicit backing-store boundary identities before application-isolation verdicts, and a cancellation-aware linked rotation operation that takes the opaque key-ID snapshot and invokes the callback in one bounded scheduled operation, returns a key absent from that snapshot, proves the recreated provider adopts exactly one matching active, non-revoked key, and preserves pre- and post-rotation canaries.
- Attempts exactly-once best-effort cleanup of every distinct disposable provider after operations and owned cancellation notifications finish, including early-exit and late-timeout completions, and bounds lifecycle admission and callback scheduling without exposing callback exceptions or secret material.
- Signals active operation tokens before timeout or cancellation results without waiting for consumer registrations; retains cancellation resources and providers through operation and notification completion.
- Keeps timeout observation reliable when synchronous callbacks block under concurrent cross-platform test or application activity by running bounded callback work in dedicated scheduler slots.
- Generates synthetic ephemeral canaries and sanitized diagnostics that exclude key material, key XML, provider configuration, and plaintext payloads.
- Targets `net8.0`, remains provider-neutral, and has no telemetry or provider SDK dependency.
- Documents Data Protection key auto-generation side effects and the tested filesystem/package-consumer evidence on Windows, Linux, and macOS.
