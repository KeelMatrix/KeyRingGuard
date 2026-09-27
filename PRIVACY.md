# Privacy

KeelMatrix.KeyRingGuard performs verification locally and does not collect or transmit telemetry. It does not contact a network unless the caller-supplied provider factory does so.

Verification uses a random, bounded, in-memory canary. The canary, protected payload, key XML, key identifiers, backing-store paths, application names, and provider exception text are not returned in diagnostics.

Timeout results do not change this privacy boundary. A synchronous caller callback that continues after a timeout remains outside KeyRingGuard's diagnostics and telemetry surface; its provider is retained until the callback completes and is then disposed. Rotation callbacks must honor cancellation before changing caller-owned key-store state.
