# Changelog

## 1.0.0

First public release.

- Fluent store declaration (`DataStore<TContext>` / `UseEntity`) compiled by a Roslyn
  incremental source generator into strongly typed `...EntityStore` / `...ReadOnly` classes.
- Lock-free snapshot reads: sequential-index arrays, dictionary indexes (single and
  composite), materialized and scan-backed named sorts, struct-specialized named predicates.
- EF Core-backed writes: `Create`/`Update`/`Delete` (+ `Range` variants), `Truncate` with
  identity reset, multi-store transactions with staged writes and a single `SaveChanges`.
- Navigation support: `UseMany` children (single and composite keys), `UseManyToMany` joins,
  `UseOwned` owned/complex types, primitive collections (`List<T>` / `T[]`).
- `UseUnion` polymorphic read views over several entity stores.
- Extension hooks (`Before*`) with optional property values and per-operation write context.
- Full-text `SearchExtension` with latinized `SearchQuery`.
- Safety features: copy-on-insert / copy-on-stage clones, sequential-index guard,
  duplicate-staging detection.
- Compile-time diagnostics E1-E12 for store misconfiguration, including the E11 guard for
  properties the store would otherwise silently drop.
