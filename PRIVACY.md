# Privacy

KeelMatrix.KeyRingGuard performs verification locally and does not collect or transmit telemetry. It does not contact a network unless the caller-supplied provider factory does so.

Verification uses a random, bounded, in-memory canary. The canary, protected payload, key XML, key identifiers, backing-store paths, application names, and provider exception text are not returned in diagnostics.

Timeout results do not change this privacy boundary. A synchronous caller callback that continues after a timeout remains outside KeyRingGuard's diagnostics and telemetry surface; its provider is retained until the callback completes and is then disposed. Callbacks cannot start a nested KeyRingGuard verification; re-entry returns `InvalidScenario` before another scheduled operation can expose caller state. Rotation snapshots only opaque key IDs in the same bounded scheduled operation immediately before invoking the callback to reject no-op/pre-existing-key callbacks; those IDs are not returned in diagnostics. Rotation callbacks must honor cancellation before changing caller-owned key-store state.
