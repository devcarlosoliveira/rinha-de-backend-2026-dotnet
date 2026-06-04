# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Submission for **Rinha de Backend 2026**: a fraud-detection endpoint that does k-NN
(k=5) vector search over **3,000,000 reference vectors of 14 dimensions**. Built in
**.NET 10 Native AOT** with an **IVF (k-means) index**, **int8-quantized** vectors, and
**SIMD AVX2** squared-euclidean distance. `fraud_score = fraudCount/5`,
`approved = fraud_score < 0.6`.

`ANALISE.md` is the design document and the source of truth for every decision; the code
is expected to follow it. Comments and docs are in **Portuguese (pt-BR)** — match that
when editing.

## The constraint that governs everything

The entire stack (nginx + 2 API instances) must fit in **1.0 CPU + 350 MB total**
(nginx 0.10/32MB, each api 0.45/159MB). This single budget is why:
- vectors are **int8**, not float32 (two float32 copies = 336 MB, won't fit);
- the runtime is **Native AOT** (no warmup, low/predictable RSS, fast startup);
- brute-force k-NN is impossible (3M×14 per query ≈ 38 GB/s memory bandwidth at 900 rps),
  hence the IVF index that touches only a few buckets per query.

Target test hardware is **Haswell (AVX2 yes, AVX-512 no)** — never use AVX-512 intrinsics.

## Architecture

```
client :9999 → nginx (round-robin) → api1 / api2   (each loads the full int8 index)
```

Two-phase split, connected only by `index.bin`:

- **Build time (no resource limits)** — `src/Rinha.IndexBuilder` reads
  `references.json.gz` (3M vectors), runs k-means, reorders vectors by bucket, quantizes
  to int8, and serializes `index.bin`.
- **Run time (inside the budget)** — `src/Rinha.Api` only *reads* `index.bin` and serves
  queries. It never builds anything.

Projects:
- `src/Rinha.Core/` — **shared, AOT-compatible** library. The whole pipeline:
  `Vectorizer` (14 dims), `Quantizer` (int8 + per-dim weights), `IvfIndex`
  (load + SIMD search), `PayloadParser` (`Utf8JsonReader`, no reflection). Used by the
  API *and* both offline tools so build/runtime can never diverge.
- `src/Rinha.IndexBuilder/` — console: `build` and `selftest` (`Program.cs` dispatches).
- `src/Rinha.Api/` — Native AOT minimal API: `GET /ready`, `POST /fraud-score`.
- `tools/Rinha.Validate/` — offline harness: FP/FN, accuracy, simulated p50/p99, estimated
  detection score, sweeping `nprobe`.

`index.bin` layout is defined in `src/Rinha.Core/IndexFormat.cs` (magic `RINH`, versioned):
header → centroids (float32) → offsets (prefix-sum) → vectors (int8, **reordered by
bucket**) → labels (bitset). **Any change to the on-disk layout must bump `Version` and
requires rebuilding the index.**

Query flow (`IvfIndex.Search`): vectorize+quantize query → find `nprobe` nearest centroids
in float → scan only those buckets in int8 (weighted, SIMD) → keep top-5 → score.

## Invariants you must preserve

These are easy to break and each break silently corrupts results:

1. **`Vectorizer` is the single source of truth for the 14 dims.** It runs at build time
   (on the 3M references) and at query time. If you change vectorization, you **must
   rebuild `index.bin`**, or queries and references live in different spaces. `selftest`
   pins the output against the two documented examples — keep it green.
2. **Quantization + distance weights are duplicated in three places that must agree:**
   `Quantizer.QuantizeDim`/`DimWeight` (scalar reference), `IvfIndex.VecDist` scalar
   fallback, and `IvfIndex.WeightS` (the SIMD weight vector). Dims **5 and 6** are special
   everywhere: sentinel `-1` (no `last_transaction`) is kept as a literal value, quantized
   as `(x+1)*127.5` (so −1→0), and given **weight 4** (applied as ×2 on the diff before
   squaring in SIMD). All other dims are `[0,1]`→`round(x*255)`, weight 1.
3. **SIMD reads 16 bytes** (dims 14,15 are zeroed via `WeightS` lanes). `IvfIndex.Load`
   allocates the vectors array with **+16 bytes of padding** so the last record can be read
   16-wide safely. Preserve both if you touch the SIMD path; gate it on `Avx2.IsSupported`
   with the scalar fallback intact.
4. **Hot path stays AOT-safe and allocation-free.** No reflection-based JSON. Responses are
   the 6 pre-serialized byte arrays in `src/Rinha.Api/Responses.cs` — written directly, no
   serialization. `Process` is deliberately **synchronous** to allow `stackalloc`. The
   index is read-only ⇒ lock-free; do not parallelize a single query.

## Correctness traps (each becomes an FP/FN) — see ANALISE.md §11

- `approved = fraud_score < 0.6` (0.6 itself = denied).
- Test payloads contain **future dates** (e.g. 2027) — never assume 2026; compute
  `hour`/`day_of_week` in **UTC** (`day_of_week`: Mon=0 … Sun=6).
- **Unknown MCC → 0.5** default (test data sends MCCs not in the table).
- `known_merchants` can contain **duplicates** — membership test only.
- Guard `avg_amount == 0` in `amount_vs_avg`.
- Clamp `[0,1]` everywhere **except** the `-1` sentinel of dims 5,6 — do not filter `-1`.
- On any internal error, return fast **`approved:true` / `fraud_score:0.0`**
  (`Responses.Fallback`), never HTTP 500: a 500 still counts toward the hard 15%-failure
  cutoff that zeroes the score, and FN weighs 3× vs FP 1×.

## Commands

There is **no `.sln`** and **no unit-test framework**; build/run target individual
projects. Requires **.NET SDK 10.0.300**. "Tests" = the two items under Validation below.

```bash
# Validate vectorization against the documented examples (fast sanity check)
dotnet run -c Release --project src/Rinha.IndexBuilder -- selftest

# Build the index (needs the official references.json.gz; ~minutes on 3M vectors)
./scripts/build-index.sh ../rinha-de-backend-2026/resources/references.json.gz
#   wraps: dotnet run -c Release --project src/Rinha.IndexBuilder -- \
#            build <ref.json.gz> artifacts/index.bin --k 2048 --iters 12

# Run the API locally against a prebuilt index
INDEX_PATH=$PWD/artifacts/index.bin dotnet run -c Release --project src/Rinha.Api
# GET /ready ; POST /fraud-score (port 9999, or $PORT)

# Publish the Native AOT binary (needs clang + zlib1g-dev; done in the Dockerfile)
dotnet publish src/Rinha.Api -c Release -r linux-x64 -o /app
```

### Validation / tuning

```bash
# Offline harness: FP/FN, accuracy, p50/p99, detection score, swept over nprobe
dotnet run -c Release --project tools/Rinha.Validate -- \
    artifacts/index.bin ../rinha-de-backend-2026/test/test-data.json \
    --nprobe 4,8,16,32 [--limit N]
```

Calibration knobs (precision ↔ speed):
- **`NPROBE`** (runtime env, default 16): buckets scanned per query. Higher = more accurate,
  slower.
- **`--k`** (centroids, default 2048) and **`--iters`** (default 12): k-means, build-time.

Rule against the harness: **never** feed `test/test-data.json` payloads into the reference
index. Stay far from the 15%-failure cutoff rather than shaving the last errors.

### Docker / submission

```bash
# Build + push the public linux/amd64 image (the Dockerfile builds index.bin inside it
# from resources/references.json.gz, which must be present)
export API_IMAGE=your-user/rinha-2026-dotnet:latest
./scripts/build-and-push.sh

# Run the full stack locally
docker compose up -d
```

`main` carries the source; the `submission` branch carries only `docker-compose.yml` +
`nginx.conf` (referencing the public image) + `info.json`.
