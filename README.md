# Rinha de Backend 2026 — .NET Native AOT

Detecção de fraude por **busca vetorial** (k-NN, k=5) sobre 3.000.000 de vetores de 14
dimensões, em **.NET 10 Native AOT**. Índice **IVF** (k-means) com vetores quantizados em
**int8** e distância euclidiana em **SIMD AVX2**. Decisões e justificativas em [ANALISE.md](./ANALISE.md).

## Arquitetura

```
cliente :9999 → nginx (round-robin) → api1 / api2   (cada uma com o índice em int8)
```

| Serviço | CPU | Memória | Papel |
|---|---|---|---|
| nginx | 0.10 | 32 MB | distribui (sem lógica) |
| api1 / api2 | 0.45 cada | 159 MB cada | vetoriza + busca IVF |
| **Total** | **1.00** | **350 MB** | dentro do limite |

## Resultados validados localmente

Harness sobre os 54.100 payloads rotulados (`test/test-data.json`), `nprobe=16`:

| Métrica | Valor |
|---|---|
| Acerto (TP+TN) | **99,64%** |
| Falhas (FP+FN) | 0,36% |
| Latência da busca (SIMD, single-thread) | p50 ~144 µs |
| Binário nativo | 7,8 MB |
| RSS por instância (índice carregado) | ~56 MB |

> A latência final (p99 sob carga do k6) depende do hardware de teste. Ajuste `NPROBE`
> (env) para trocar precisão por velocidade — veja a tabela de varredura no harness.

## Estrutura

```
src/Rinha.Core/         vetorização (14 dims), quantização int8, IVF (leitura+busca SIMD), parser
src/Rinha.IndexBuilder/ console: references.json.gz → index.bin (k-means)
src/Rinha.Api/          API Native AOT (Kestrel minimal: /ready, /fraud-score)
tools/Rinha.Validate/   harness offline: FP/FN, latência e score estimado
Dockerfile, docker-compose.yml, nginx.conf
```

## Validar a lógica localmente (sem Docker)

```bash
# 1. testa a vetorização contra os exemplos da documentação
dotnet run -c Release --project src/Rinha.IndexBuilder -- selftest

# 2. constrói o índice (precisa das referências da Rinha)
./scripts/build-index.sh ../rinha-de-backend-2026/resources/references.json.gz

# 3. mede FP/FN e latência, varrendo nprobe
dotnet run -c Release --project tools/Rinha.Validate -- \
    artifacts/index.bin ../rinha-de-backend-2026/test/test-data.json --nprobe 4,8,16,32

# 4. sobe a API e testa via HTTP
INDEX_PATH=$PWD/artifacts/index.bin dotnet run -c Release --project src/Rinha.Api
```

## Construir e publicar a imagem (submissão)

A imagem precisa ser **pública** e `linux/amd64`. O `Dockerfile` compila o binário AOT e
constrói o `index.bin` a partir de `resources/references.json.gz` (coloque o arquivo nesse
caminho, copiado do repositório oficial da Rinha).

```bash
cp ../rinha-de-backend-2026/resources/references.json.gz resources/
export API_IMAGE=seu-usuario/rinha-2026-dotnet:latest
./scripts/build-and-push.sh
```

## Rodar local com Docker e testar com k6

```bash
export API_IMAGE=seu-usuario/rinha-2026-dotnet:latest   # imagem local ou publicada
docker compose up -d
# em outro terminal, a partir do repositório da Rinha:
cd ../rinha-de-backend-2026/test && docker compose --profile test up
```

## Submissão

- Branch `main`: este código-fonte. Branch `submission`: só `docker-compose.yml` + `nginx.conf`
  (referenciando a imagem pública) + `info.json`.
- Edite `info.json` e o `LICENSE` (MIT) com seu nome/usuário.
- Abra o PR em `participants/<seu-usuario>.json` e a issue `rinha/test` para rodar a prévia.

## Ajuste fino (`NPROBE`)

`NPROBE` controla quantos buckets do IVF são varridos. Maior = mais preciso e mais lento.
Bom ponto de partida: **16**. Calibre com o harness e com as prévias oficiais.
