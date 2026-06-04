#!/usr/bin/env bash
# Constrói o index.bin localmente a partir das referências da Rinha.
set -euo pipefail
cd "$(dirname "$0")/.."

REF="${1:-../rinha-de-backend-2026/resources/references.json.gz}"
OUT="${2:-artifacts/index.bin}"

[ -f "$REF" ] || { echo "referências não encontradas: $REF"; exit 1; }

dotnet run -c Release --project src/Rinha.IndexBuilder -- build "$REF" "$OUT" --k 2048 --iters 12
echo "índice em: $OUT"
