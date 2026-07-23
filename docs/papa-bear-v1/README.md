# Papa Bear v1 documentation

This directory contains the durable product and architecture specifications for
Papa Bear.

## Current baseline and roadmap

The current source baseline is **0.9.1 — Robust Context-on-Demand**. It is
integrated on `main`; it is not represented by a Git tag or published GitHub
Release, and full live 0.9.1 Arma acceptance remains pending.

There is no approved next-milestone roadmap in the repository. Historical
milestone plans, release plans and superseded implementation roadmaps were
deliberately removed. A future plan becomes authoritative only when it is
explicitly approved and added as a new specification.

See [`current-status.md`](current-status.md) for the release/acceptance matrix
and [`../../CHANGELOG.md`](../../CHANGELOG.md) for completed history.

## Active specifications

1. [`product-vision.md`](product-vision.md) — current product goals and
   non-goals.
2. [`persona-and-dialog.md`](persona-and-dialog.md) — Papa Bear role and radio
   behavior.
3. [`world-model.md`](world-model.md) — current state, contact history, mission
   memory, provenance, freshness and uncertainty.
4. [`arma-data-contract.md`](arma-data-contract.md) — active telemetry and
   local/model query boundaries.
5. [`privacy-and-fair-play.md`](privacy-and-fair-play.md) — multiplayer,
   privacy, retention and authority boundaries.
6. [`voice-architecture.md`](voice-architecture.md) — current transcription,
   context-on-demand reasoning and speech path.

These files define the durable current-product contract. Schemas under
`../../schemas/`, tests and source code provide the executable detail.

Until a new milestone specification is added, changes must preserve the active
product boundaries and avoid expanding into deferred capabilities without an
explicitly approved specification.

The changelog is descriptive history, not a roadmap. A version heading,
historical experiment or deferred item does not authorize new implementation.
