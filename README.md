# Puppeteer

Puppeteer is a runtime that combines CQRS, the Actor Model, and Event Sourcing with a domain-specific language whose programs are journaled as the unit of persistence. It exists as the instantiation referenced by a series of design theory papers; this repository records that the constructs those papers define are realizable in working code.

## Start with the papers

The primary surface for understanding why this code exists is the companion repository:

> **[alvaroNCubo/puppeteer-papers](https://github.com/alvaroNCubo/puppeteer-papers)** — seven design theory papers introducing the constructs (porosity, program–value separability, the now/deferred partition, cross-actor continuity, journal-as-substrate, infrastructural symptom, server as accidental category) and tracing their consequences.

Each paper treats Puppeteer as one system that happens to satisfy the conditions the construct defines. The contribution of the series is conceptual; this repository is the existence proof.

## What lives here

- `Puppeteer/` — the core runtime: the actor host, the journaled DSL interpreter, the event-sourcing storage backends, and the reflection-based domain loader.
- `Choreography/` — the cross-actor extension: stage management, transports (HTTPS and SimpleX), dispatch, sagas, and the reactions surface that carries semantic continuity across actors.

## Status

Working draft tied to ongoing research. Pre-1.0. The API surface is not stabilized and is expected to move alongside the papers.

## Build

```
dotnet build Puppeteer.sln
```

The test suite is not included in this repository. Tests run in the author's private development environment against the same sources; the published source compiles independently and is the subject of the papers' empirical sections.

## License

Code in this repository is licensed under the [Apache License 2.0](LICENSE). The papers and their accompanying assets in the companion repository are licensed separately under CC BY 4.0; see that repository for terms governing citation and quotation of the papers themselves.

## Attribution and citation

The papers in [alvaroNCubo/puppeteer-papers](https://github.com/alvaroNCubo/puppeteer-papers) are the canonical citation surface for the constructs and conditions named in this work. This repository is the running instantiation referenced by the papers' empirical sections; cite the corresponding paper rather than the code when referring to a construct, condition, or claim.

Author: Alvaro Rivera (Ncubo Ideas).

Copyright © 2026 Alvaro Rivera.
