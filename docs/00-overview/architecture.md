# Architecture

## Purpose and lifecycle

Public .NET release projection; canonical source is in NPS-Dev.

## Boundaries

src/: packages.

tests/: tests.

samples/: examples.

benchmarks/: benchmarks.

spec/: released snapshot.

Keep responsibilities and dependency direction explicit. Do not add layers or cross-repository sources of truth without a concrete requirement. Record architecture-significant decisions under `docs/04-decisions/`.
