# syntax=docker/dockerfile:1

# ---------- build: publica o binário AOT e constrói o index.bin ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
RUN apt-get update \
 && apt-get install -y --no-install-recommends clang zlib1g-dev \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY . .

# Binário nativo da API (Native AOT, linux-x64)
RUN dotnet publish src/Rinha.Api -c Release -r linux-x64 -o /app

# Índice IVF (int16, escala 10000, v5) a partir das referências.
# Para acelerar builds, troque por: COPY artifacts/index.bin /app/index.bin
RUN dotnet run -c Release --project src/Rinha.IndexBuilder -- \
        build resources/references.json.gz /app/index.bin --k 2048 --iters 12

# ---------- final: debian-slim (roda como root => cria o .sock no volume partilhado) ----------
FROM debian:bookworm-slim AS final
WORKDIR /app
COPY --from=build /app/Rinha.Api /app/Rinha.Api
COPY --from=build /app/index.bin /app/index.bin
ENV INDEX_PATH=/app/index.bin \
    NLOW=8 \
    NHIGH=128 \
    PORT=9999 \
    DOTNET_gcServer=0
EXPOSE 9999
ENTRYPOINT ["/app/Rinha.Api"]
