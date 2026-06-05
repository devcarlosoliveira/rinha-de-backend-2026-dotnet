# syntax=docker/dockerfile:1

# ---------- index: constrói o index.bin com .NET (sem limites; não vai pra imagem final) ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS index
WORKDIR /src
COPY . .
# Índice IVF (int16, escala 10000, v5) a partir das referências.
# Para acelerar builds, troque por: COPY artifacts/index.bin /index.bin
RUN dotnet run -c Release --project src/Rinha.IndexBuilder -- \
        build resources/references.json.gz /index.bin --k 2048 --iters 12

# ---------- rust: compila o servidor (sem GC, event loop mono-thread) ----------
FROM rust:1-bookworm AS rust
WORKDIR /app
COPY api-rs/Cargo.toml api-rs/Cargo.lock ./
COPY api-rs/src ./src
# Baseline x86-64-v2 (Haswell ok); AVX2 é detectado em runtime (is_x86_feature_detected).
ENV RUSTFLAGS="-C target-cpu=x86-64-v2"
RUN cargo build --release --locked

# ---------- final: imagem mínima só com o binário + índice ----------
FROM debian:bookworm-slim AS final
COPY --from=rust /app/target/release/rinha-api /rinha-api
COPY --from=index /index.bin /index.bin
ENV INDEX_PATH=/index.bin \
    NLOW=8 \
    NHIGH=128 \
    PORT=9999
EXPOSE 9999
ENTRYPOINT ["/rinha-api"]
