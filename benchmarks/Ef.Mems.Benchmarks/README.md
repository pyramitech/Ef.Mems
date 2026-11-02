# Ef.Mems.Benchmarks

Delegate predicates (`Query(x => ...)`) vs struct predicates (`UsePredicate` /
`IPredicate<T>`) over a fully cached store of 1,000,000 scalar entities, plus
SearchQuery engine benchmarks over 4,000,000 entities (production scale).

```
dotnet run -c Release -- --filter "*"              # full run
dotnet run -c Release -- --filter "*" --job short  # quicker, indicative
```

The seeded SQLite file is created once in `%TEMP%\efmems_bench_1000000.db`
and reused across runs.

## Results (short job, .NET 9, Ryzen 9 7950X3D under VirtualBox)

Default runtime (dynamic PGO on):

| Method                  | Categories              | Mean     | Ratio | Allocated |
|------------------------ |------------------------ |---------:|------:|----------:|
| Count_Delegate          | Count full scan         | 5.13 ms  |  1.00 |     168 B |
| Count_StructNamed       | Count full scan         | 5.82 ms  |  1.14 |     104 B |
| Count_StructHandwritten | Count full scan         | 5.34 ms  |  1.04 |     104 B |
| First_Delegate          | FirstOrDefault no match | 4.59 ms  |  1.00 |         - |
| First_StructNamed       | FirstOrDefault no match | 5.09 ms  |  1.11 |         - |
| Page_Delegate           | Paged query             | 1.66 us  |  1.00 |     704 B |
| Page_StructNamed        | Paged query             | 1.63 us  |  0.98 |     640 B |

Polymorphic call site (default runtime, PGO on; four distinct predicates
share the scan loop, mean is per scan via `OperationsPerInvoke = 4`):

| Method             | Mean     | Ratio | Allocated |
|------------------- |---------:|------:|----------:|
| CountPoly_Delegate | 10.31 ms |  1.00 |     114 B |
| CountPoly_Struct   |  6.15 ms |  0.60 |      50 B |

With `DOTNET_TieredPGO=0` (Count category):

| Method                  | Mean     | Ratio | Allocated |
|------------------------ |---------:|------:|----------:|
| Count_Delegate          | 11.37 ms |  1.00 |     168 B |
| Count_StructNamed       |  4.90 ms |  0.43 |     104 B |
| Count_StructHandwritten |  5.09 ms |  0.45 |     104 B |

## Interpretation

- In the single-predicate benchmarks each process funnels **one** lambda
  through the shared scan loop, so dynamic PGO sees a monomorphic call site
  and guarded-devirtualizes + inlines the delegate call - the delegate's
  best case - and the two approaches tie (differences are within
  short-run/VM noise).
- A real store runs **many different predicates** through the same loop
  instantiation. The `Count polymorphic` category models this directly:
  four syntactically distinct lambdas (identical bodies) rotate through the
  loop, the call-site profile is split ~25% four ways - below the
  devirtualization likelihood threshold - and the delegate pays the honest
  indirect-call cost (10.3 ms vs 5.1 ms monomorphic) *with PGO fully on*.
  Each struct predicate type still gets its own specialized loop, so the
  struct side stays flat and wins ~1.7x.
- The `DOTNET_TieredPGO=0` run cross-checks the same effect by removing the
  profile instead of polluting it: 11.4 ms delegate vs 4.9 ms struct
  (~2.3x). Both experiments agree on the mechanism.
- Struct predicates are unaffected by PGO in every configuration
  (4.9-6.1 ms across all runs): their inlining is guaranteed by generic
  specialization, not won by JIT profiling, and they never allocate the
  per-call delegate.

## SearchQuery engine (SearchBenchmarks, 4M rows, short job)

Per-token ordinal `string.Contains` (original engine) vs one single-string
`SearchValues` per token (shipped engine since this benchmark). QueryCount
with `take: 0`; SearchField is "{color} {noun} {extra} unit {n}".

| Method                 | Categories          | N         | Mean     | Ratio | Allocated |
|----------------------- |-------------------- |----------:|---------:|------:|----------:|
| NoMatch_Contains       | Search no match     |     4,000 | 85.15 us |  1.00 |      32 B |
| NoMatch_SearchValues   | Search no match     |     4,000 | 45.31 us |  0.53 |      32 B |
| NoMatch_Contains       | Search no match     | 4,000,000 | 91.49 ms |  1.00 |      32 B |
| NoMatch_SearchValues   | Search no match     | 4,000,000 | 54.12 ms |  0.59 |      32 B |
| Common_Contains        | Search common token |     4,000 | 91.37 us |  1.00 |     104 B |
| Common_SearchValues    | Search common token |     4,000 | 37.94 us |  0.42 |     104 B |
| Common_Contains        | Search common token | 4,000,000 | 94.22 ms |  1.00 |     104 B |
| Common_SearchValues    | Search common token | 4,000,000 | 47.60 ms |  0.51 |     104 B |
| TwoTokens_Contains     | Search two tokens   |     4,000 | 89.02 us |  1.00 |      32 B |
| TwoTokens_SearchValues | Search two tokens   |     4,000 | 40.07 us |  0.45 |      32 B |
| TwoTokens_Contains     | Search two tokens   | 4,000,000 | 93.24 ms |  1.00 |      32 B |
| TwoTokens_SearchValues | Search two tokens   | 4,000,000 | 49.48 ms |  0.53 |      32 B |

- `SearchValues.Create` preprocesses each token once (rare-character anchor
  selection); `Contains` re-derives its strategy per call and always anchors
  on the needle's first/last characters. Amortized over a 4M-row scan the
  preprocessing wins ~2x, so `SearchQuery` now stores
  `SearchValues<string>[]` and probes with `IndexOfAny`.
- All three shapes cost the same per engine: AND semantics short-circuit on
  the first missing token, so extra tokens are nearly free unless the first
  token matches (~23 ns/row Contains vs ~12 ns/row SearchValues).
- Scaling is linear with no visible fixed overhead: per-row cost is the same
  at 4k and 4M rows (the ratio is even slightly better at 4k, where the
  whole table sits in CPU cache and memory latency masks less of the
  compute difference). A few-thousand-row search costs ~40-90 us total with
  either engine.

## QuerySortCount (SortBenchmarks, 4M rows, short job)

Delegate baseline (Func keySelector + IComparer) vs the fully specialized
generic overload (struct predicate + IKeySelector + KeyComparer, direction
folded into the comparer). Predicate matches ~half the rows (~2M).

| Method            | Categories      | Mean      | Ratio | Allocated |
|------------------ |---------------- |----------:|------:|----------:|
| Top50_Delegate    | Sort top50      |  48.13 ms |  1.00 |     624 B |
| Top50_Struct      | Sort top50      |  37.25 ms |  0.77 |     616 B |
| DeepPage_Delegate | Sort deep page  | 298.64 ms |  1.00 |     624 B |
| DeepPage_Struct   | Sort deep page  | 168.16 ms |  0.56 |     616 B |
| ByIdDesc_Delegate | Sort by Id desc | 226.56 ms |  1.00 |     624 B |
| ByIdDesc_Struct   | Sort by Id desc | 218.24 ms |  0.96 |     616 B |

- The struct overload wins where comparisons dominate: 1.3x on a plain
  top-50 and 1.8x on a deep page (skip 100k => 100 050-entry heap, ~17
  compares per improving element - all inlined in the struct variant).
- Deep paging is inherently degenerate (48 ms -> 299 ms just from the
  skip), confirming the hybrid collect-and-sort idea if deep pages matter.
- "Latest 50 by Id desc" is the pathological heap case: ids arrive in
  ascending scan order, so nearly every matching row improves the heap and
  pays a full sift - 226 ms for a query the sorted-by-construction seq
  array could answer in microseconds. That is the motivation for the
  planned order-by-sequential-index fast path; this row is its baseline.
- Allocations are flat (~620 B) in all variants: the rented heap is pooled
  and returned (leak fixed), and the closure comparer per query is gone.

## Sorted fast paths (descending + UseSort, 4M rows, short job)

| Method                            | Categories           | Mean       | Ratio |
|---------------------------------- |--------------------- |-----------:|------:|
| ByIdDesc_Delegate (heap)          | Sort by Id desc      | 228.22 ms  | 1.000 |
| ByIdDesc_Struct (heap)            | Sort by Id desc      | 216.69 ms  | 0.950 |
| ByIdDesc_SeqWalk                  | Sort by Id desc      | **426 ns** | 0.000 |
| NamedSort_Scan                    | Named sort top50     |  33.52 ms  |  1.00 |
| NamedSort_Materialized (count)    | Named sort top50     |  35.64 ms  |  1.06 |
| NamedSort_MaterializedNoCount     | Named sort top50     |  30.34 ms  |  0.91 |
| NamedSortDesc_Scan                | Named sort desc t50  |  37.42 ms  | 1.000 |
| NamedSortDesc_MaterializedNoCount | Named sort desc t50  | **592 ns** | 0.000 |

- "Latest 50 by Id": Query(pred, 0, 50, descending: true) walks the seq
  snapshot back to front and early-exits - 426 ns vs 228 ms for the heap
  (~535,000x). The descending flag exists on all seq-store query wrappers.
- Materialized sorts (UseSort materialized: true) are direction-sensitive:
  a window aligned with where the matches sit early-exits in ~50 elements
  (592 ns vs 37 ms scan, ~63,000x); an ANTI-aligned predicate (min-filter
  walked ascending) scans the non-matching prefix first (~30 ms). Rule of
  thumb: materialized sorts shine for windowed reads without MatchCount,
  in the direction your UI pages.
- MatchCount forces visiting every element, where the sorted array cannot
  help (and its pointer order is cache-hostile to walk - it measured 59 ms
  before the fix), so QuerySortCount{Name} on a materialized sort routes to
  the scan engine: with-count queries cost the same with or without
  materialization (~34 ms). A possible future hybrid (items from the sorted
  walk + count from a dense seq scan) could roughly halve that if counted
  paging ever becomes a hotspot.
- Write cost of materialized sorts: one O(n) copy of the sorted array per
  write to the entity (documented on UseSort) - use for read-mostly tables.
