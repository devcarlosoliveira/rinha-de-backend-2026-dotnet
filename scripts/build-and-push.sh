#!/usr/bin/env bash
# Constrói e publica a imagem pública (linux/amd64) usada na submissão.
set -euo pipefail
cd "$(dirname "$0")/.."

: "${API_IMAGE:?defina API_IMAGE, ex: export API_IMAGE=seu-usuario/rinha-2026-dotnet:latest}"

# O Dockerfile constrói o índice dentro da imagem a partir deste arquivo:
[ -f resources/references.json.gz ] || {
  echo "coloque resources/references.json.gz (copie do repositório oficial da Rinha)"; exit 1; }

docker buildx build --platform linux/amd64 -t "$API_IMAGE" --push .
echo "publicado: $API_IMAGE"
