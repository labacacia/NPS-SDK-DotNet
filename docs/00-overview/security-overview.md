# Security Overview

Treat frames, endpoints, certificates, keys, and serialized data as untrusted. Closely review parsing, crypto, identity, network, dependency, and publishing changes.

## Review rules

- Validate external input and fail closed.
- Keep secrets out of source, fixtures, logs, and errors.
- Bound network, parser, file, memory, and concurrency work where applicable.
- Pin and review automation dependencies.
- Add focused tests for changed security behavior.
- Follow [SECURITY.md](../../SECURITY.md) for reports.
