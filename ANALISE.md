# Rinha de Backend 2026 — Análise e Blueprint (.NET Native AOT)

> Documento-fonte da solução. O código deve seguir o que está aqui. Decisões marcadas
> com **(tunar)** são parâmetros a calibrar com o harness de validação.

## 1. Objetivo

Construir o módulo de **detecção de fraude por busca vetorial**:

- `POST /fraud-score` — vetoriza a transação em 14 dimensões, acha os 5 vizinhos mais
  próximos em um dataset de 3.000.000 vetores rotulados, `fraud_score = nº_fraudes / 5`,
  responde `approved = fraud_score < 0.6`.
- `GET /ready` — `2xx` quando pronto.
- Porta **9999** (recebida pelo load balancer).

Prazo de submissão: **2026-06-05 23:59 -03**.

## 2. Restrições que governam o projeto

| Restrição | Valor | Implicação |
|---|---|---|
| Orçamento total | **1.0 CPU + 350 MB** (LB + 2 APIs + tudo) | Sem 2 cópias float32 → **quantizar int8** |
| Carga (k6) | rampa **1→900 rps em 120s**, timeout 2001ms | ~54k reqs; ~450 rps/instância em ~0.45 CPU |
| Topologia | LB + **≥2 instâncias**, round-robin, LB sem lógica | nginx round-robin + 2 APIs idênticas |
| Rede | `bridge` (sem `host`/`privileged`) | — |
| p99 → score | **1ms=+3000**, 10ms=+2000, 100ms=+1000, >2000ms=−3000 | Precisa de índice; força bruta não fecha |
| Detecção → score | erro ponderado `1·FP+3·FN+5·Err`; **corte rígido em 15% de falhas → −3000** | Ficar longe do corte > raspar erros |

**Por que força bruta está descartada:** k-NN exato varre 3M×14 por consulta = 42 MB lidos
em int8. A 900 rps ≈ **38 GB/s** de banda de memória, acima do Mac Mini 2014 e ainda
compartilhada. → Precisa de índice que toque ~milhares de vetores por consulta.

## 3. Ambiente

- Teste oficial: **Mac Mini Late 2014, 2.6 GHz, 8 GB, Ubuntu 24.04, amd64** (Haswell →
  **AVX2 sim, AVX-512 não**).
- Dev local: x86_64, **.NET SDK 10.0.300**, **Docker ausente** (lógica validada via `dotnet`;
  `docker-compose`/`Dockerfile` validados pelo usuário).

## 4. Dataset (números reais conferidos)

- `references.json.gz`: 50 MB gz → **298 MB** cru, **3.000.000** registros `{vector[14], label}`.
- **33,3% fraude**, 66,7% legit. **20,0%** com sentinela `-1` (sem `last_transaction`).
- Faixa por dim (cru): a maioria em `[0,1]`; **dim 13 quase constante** (`[0.002, 0.05]`);
  dims 5,6 ∈ `{-1} ∪ [0,1]`; dims discretas de baixa cardinalidade (hora, dow, flags, mcc).
- `mcc_risk.json` (10 valores) e `normalization.json` — abaixo.

## 5. Stack escolhida

**.NET 10 Native AOT + Kestrel/Minimal API + índice IVF (k-means) quantizado int8 + nginx
round-robin.** AOT: sem warmup, startup <1s, memória residente baixa/previsível, SIMD via
`System.Runtime.Intrinsics` (AVX2).

## 6. Arquitetura e divisão de recursos

```
Client :9999 → nginx (round-robin) → api1 / api2   (cada uma com o índice próprio em int8)
```

| Serviço | CPU | Memória | Papel |
|---|---|---|---|
| nginx | 0.10 | 32 MB | só distribui |
| api1 | 0.45 | 159 MB | vetoriza + busca IVF |
| api2 | 0.45 | 159 MB | idem |
| **Total** | **1.00** | **350 MB** | ✓ |

Índice carregado em **arrays gerenciados** (`byte[]`/`float[]`, vão para o LOH), read-only
por toda a vida do processo. GC workstation, `DOTNET_gcServer=0`; o limite de memória do
contêiner (159 MB, detectado via cgroup) contém heap + índice (~42 MB) — sem
`DOTNET_GCHeapHardLimit` explícito.

**Custo de memória do índice (por instância):**

| Formato | Vetores | 2 instâncias | Cabe? |
|---|---|---|---|
| float32 | 168 MB | 336 MB | ❌ |
| float16 | 84 MB | 168 MB | ⚠️ fallback |
| **int8** | **42 MB** | **84 MB** | ✅ |

## 7. Vetorização — as 14 dimensões (fonte única da verdade)

`clamp(x) = min(1, max(0, x))`. Constantes de `normalization.json`:

```
max_amount=10000  max_installments=12  amount_vs_avg_ratio=10  max_minutes=1440
max_km=1000  max_tx_count_24h=20  max_merchant_avg_amount=10000
```

| i | dim | fórmula |
|---|---|---|
| 0 | amount | `clamp(amount / 10000)` |
| 1 | installments | `clamp(installments / 12)` |
| 2 | amount_vs_avg | `clamp((amount / avg_amount) / 10)` — proteger `avg==0` |
| 3 | hour_of_day | `hour(requested_at, UTC) / 23` |
| 4 | day_of_week | `dow(requested_at, UTC) / 6` (seg=0 … dom=6) |
| 5 | minutes_since_last | `clamp(min / 1440)` ou **`-1`** se `last_transaction==null` |
| 6 | km_from_last | `clamp(km_from_current / 1000)` ou **`-1`** se null |
| 7 | km_from_home | `clamp(km_from_home / 1000)` |
| 8 | tx_count_24h | `clamp(tx_count_24h / 20)` |
| 9 | is_online | `1` se online senão `0` |
| 10 | card_present | `1` se card_present senão `0` |
| 11 | unknown_merchant | `1` se `merchant.id ∉ known_merchants` senão `0` |
| 12 | mcc_risk | `mcc_risk[mcc]`, **padrão `0.5`** |
| 13 | merchant_avg | `clamp(merchant.avg_amount / 10000)` |

`mcc_risk`: `5411:.15 5812:.30 5912:.20 5944:.45 7801:.80 7802:.75 7995:.85 4511:.35 5311:.25 5999:.50`.

dim 5 = minutos entre `last_transaction.timestamp` e `transaction.requested_at`.

## 8. Quantização int8 (esquema decidido)

- Dims em `[0,1]` (todas exceto 5,6): `q = round(x * 255)` → byte `[0,255]`.
- Dims 5,6 (`{-1} ∪ [0,1]`): `q = round((x + 1) * 127.5)` → `-1`→0, `0.0`→128, `1.0`→255.
  Mantém o cluster dos 20% sem histórico **separado** dos vetores recentes.
- **Distância = `Σ wᵢ·(qᵢ_query − qᵢ_ref)²`** (euclidiana ao quadrado, sem `sqrt`), com
  **`w=4` para dims 5,6** e `w=1` nas demais — restaura a escala float-equivalente
  (porque dims 5,6 usam meia-resolução por unidade). Aplicar como vetor de pesos no SIMD.
- A query é quantizada com o mesmo esquema antes da busca.

## 9. Índice IVF (o coração)

Construído **no build** (sem limites de recurso), serializado em `index.bin`, só lido no runtime.

**Build:** parse das 3M → **k-means 2048 centroides (tunar)** (Lloyd sobre amostra
~200k, depois atribui as 3M) → reordena vetores por bucket → quantiza int8 → serializa.

**`index.bin` (layout):**
```
header   : magic, version, nDims=14, nVectors=3_000_000, nCentroids
centroids: nCentroids × 14 float32   (poucos; mantém precisão na 1ª etapa)
offsets  : (nCentroids+1) × int32     (prefix-sum: início de cada bucket)
vectors  : nVectors × 14 int8         (REORDENADOS por bucket)
labels   : bitset nVectors            (fraude=1) — ~375 KB
```

**Consulta:**
1. vetoriza + quantiza a query;
2. distância query→centroides (float), pega os **`nprobe` (tunar, 8–32)** menores;
3. varre só esses buckets (int8 + pesos, SIMD AVX2 `Vector256`), mantém **top-5** (slots fixos);
4. conta fraudes nos 5 → `score = nf/5` → `approved = score < 0.6`.

`nprobe=16` ⇒ ~23k vetores ≈ 320 KB/consulta (cabe no cache) → sub-ms.

**Recall:** só consultas na fronteira (2 vs 3 fraudes) são sensíveis ao caráter aproximado;
alvo ≥99% de concordância com o k-NN exato — calibrar `nprobe`/`nCentroids` no harness.

## 10. Hot path (.NET AOT)

- HTTP: `WebApplication.CreateSlimBuilder` + Minimal API + Kestrel, keep-alive.
- JSON: parse manual com `Utf8JsonReader` direto sobre os bytes (`PayloadParser`), zero
  reflexão. Request parseado para struct; resposta tem só 6 combinações (fraudCount 0..5,
  `approved` derivado do score) →
  **pré-serializar os bytes** e escrever direto.
- SIMD: `Vector256<T>`/AVX2 com guarda `Avx2.IsSupported` (sem AVX-512).
- Índice read-only → **sem lock**, concorrência total; não paralelizar consulta única.
- Resiliência: em erro interno, responder rápido `approved:true` em vez de HTTP 500
  (peso 5 vs 1, e 500 ainda conta no corte de 15%).

## 11. Armadilhas de corretude (cada uma vira FP/FN)

1. `approved = fraud_score < 0.6` (0.6 = negado).
2. **Datas futuras** no test-data (ex.: 2027) — não assumir 2026; `hour`/`dow` em **UTC**.
3. **MCC fora da tabela → 0.5** (payloads de teste trazem MCC desconhecido).
4. `known_merchants` tem **duplicatas** — usar `Contains`.
5. `amount_vs_avg`: proteger `avg==0`.
6. Clamp `[0,1]` em tudo **exceto** o `-1` das dims 5,6; **não filtrar** os `-1`.
7. O `test.js` de prévia só lê `body.approved` (ignora `fraud_score`) — preencher correto mesmo assim.

## 12. Validação (harness offline)

- Usar `test/test-data.json` (54.100 entradas com `expected_approved`) para medir
  **FP/FN/concordância** e simular `p99` de CPU da busca. **Calibrar `nprobe`/`nCentroids`.**
- **Não inserir** payloads de teste no índice de referência (regra).
- Replicar a fórmula de pontuação (`AVALIACAO.md`) para estimar o score final localmente.

## 13. Metas de pontuação

Alvo realista: **p99 ~3–10ms (+2000…+2500)** + detecção quase perfeita (~+2200…+2800)
≈ **~4500–5000**. Inimigo nº1: o **corte de 15%** — ficar longe dele.

## 14. Plano de execução (fases)

1. Esqueleto: projetos `Core`, `IndexBuilder`, `Api` (csproj AOT), sem `.sln` (build por projeto).
2. `Core`: normalização + vetorização (14 dims) + quantização int8 (validação via `selftest`).
3. `IndexBuilder`: parse gz → k-means → reordena → `index.bin`.
4. `Core`: leitura `index.bin` + busca IVF SIMD (top-5, score).
5. Harness: FP/FN/concordância + p99 simulado vs `test-data.json`; tunar.
6. `Api`: Kestrel minimal (`/ready`, `/fraud-score`), parse `Utf8JsonReader`, resposta pré-serializada.
7. Empacotamento: `Dockerfile` (build do índice + AOT), `docker-compose.yml` (nginx + 2 APIs, limites), `nginx.conf`.

## 15. Estrutura de diretórios

```
rinha-de-backend-2026-dotnet/
  ANALISE.md
  src/Rinha.Core/         vetorização, quantização, IVF (build+leitura+busca)
  src/Rinha.IndexBuilder/ console: references.json.gz → index.bin
  src/Rinha.Api/          minimal API AOT
  tools/Rinha.Validate/   harness offline (FP/FN/p99/score)
  Dockerfile, docker-compose.yml, nginx.conf   (na raiz do repositório)
```

A branch `submission` levará só `docker-compose.yml` (raiz) + `nginx.conf` + imagens públicas.
Repos sob licença **MIT** e públicos; adicionar `info.json` e o PR em `participants/<github>.json`.
