# Privacy

KeelMatrix.KeyRingGuard performs verification locally and does not collect or transmit telemetry. It does not contact a network unless the caller-supplied provider factory does so.

Verification uses a random, bounded, in-memory canary. The canary, protected payload, key XML, key identifiers, backing-store paths, application names, and provider exception text are not returned in diagnostics.
