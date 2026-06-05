# Rinha de Backend 2026 — .NET Native AOT

Detecção de fraude por **k-NN exato (k=5)** sobre 3.000.000 de vetores de 14 dimensões, em
**.NET 10 Native AOT**. Índice **IVF** (k-means) com vetores **int16 (escala 10000)** e
distância euclidiana em **SIMD AVX2** — reproduz o k-NN float do avaliador com **erro zero
(E=0)**. Servido por um **event loop epoll mono-thread** próprio (sem Kestrel), pinado a um
**core dedicado** (cpuset), com o índice **travado na RAM** (mlock) e transporte LB→API por
**Unix domain socket** sobre tmpfs.

A história completa — tentativas, erros e aprendizados — está em **[JORNADA.md](./JORNADA.md)**.
As decisões de design originais em [ANALISE.md](./ANALISE.md).

## Arquitetura

```
                       ┌──────────────── tmpfs /sockets ────────────────┐
cliente :9999 → nginx (L4 stream) ──unix:/sockets/api1.sock──→ api1 (epoll, core 0)
                  cpuset 2,3      ──unix:/sockets/api2.sock──→ api2 (epoll, core 1)
```

| Serviço | CPU | core (cpuset) | Memória | Papel |
|---|---|---|---|---|
| nginx | 0.10 | 2,3 | 32 MB | LB L4 (round-robin, sem lógica) |
| api1 | 0.45 | **0** | 159 MB | event loop epoll + busca IVF |
| api2 | 0.45 | **1** | 159 MB | event loop epoll + busca IVF |
| **Total** | **1.00** | — | **350 MB** | dentro do limite |

### Por que cada peça existe (latência determinística)

| Técnica | Motivo |
|---|---|
| **epoll mono-thread** (não Kestrel/async) | um burst paralelo estoura a quota de 0.45 CPU e o CFS congela o container ~55 ms. Serializar numa thread mantém a CPU instantânea baixa. |
| **cpuset (core dedicado)** | elimina contenção/migração de scheduler — a peça decisiva para o p99 sub-ms. |
| **mlock do índice** | zero page-fault no caminho quente (precisa de `ulimit memlock=-1`). |
| **Unix domain socket (tmpfs)** | sem pilha TCP de loopback (handshake/checksum/TIME_WAIT) ⇒ menos CPU dos dois lados. |
| **int16 escala 10000 + busca adaptativa** | E=0 (detecção perfeita) varrendo ~12 buckets/req em média. |

> **io_uring** seria o passo seguinte além do epoll, mas é **bloqueado pelo seccomp padrão do
> Docker** (testado: `EPERM` no container) — por isso o epoll é o teto prático de IO.

## Resultados

| Métrica | Valor |
|---|---|
| Detecção (offline, 54.100 payloads) | **FP=0, FN=0, E=0** → detScore **3000** (teto) |
| Busca adaptativa | ~12,5 buckets/req em média |
| RSS por instância (índice + mlock) | ~99 MB / 159 MB |
| Score em prévia oficial (config async anterior) | final **4449** (det 3000 + p99 ~38 ms) |
| p99 da config epoll+cpuset | a aposta — ver [JORNADA.md](./JORNADA.md) |

## Estrutura

```
src/Rinha.Core/         vetorização (14 dims), quantização int16, IVF (leitura+busca SIMD), parser
src/Rinha.IndexBuilder/ console: references.json.gz → index.bin (k-means, v5)
src/Rinha.Api/          API Native AOT — dois runtimes:
   Epoll/                  event loop epoll mono-thread via syscalls cruas (RUNTIME=epoll)
   RawServer.cs            servidor async sobre Socket (RUNTIME=async; Kestrel-free)
tools/Rinha.Validate/   harness offline: FP/FN, latência, detScore, varredura de nprobe/adaptive
api-rs/                 experimento descartado: porta do runtime em Rust (ver JORNADA.md)
Dockerfile, docker-compose.yml, nginx.conf
```

## Variáveis de ambiente (API)

| Var | Default | Papel |
|---|---|---|
| `RUNTIME` | `async` | `epoll` (mono-thread, produção) ou `async` |
| `INDEX_PATH` | `artifacts/index.bin` | caminho do índice |
| `SOCKET_PATH` | — | se setado, escuta em Unix socket (senão TCP `PORT`) |
| `MLOCK_INDEX` | — | `1` trava todas as páginas na RAM (precisa de `memlock=-1`) |
| `NLOW` / `NHIGH` | `8` / `128` | busca adaptativa: passo barato / escala nos ambíguos |

## Validar a lógica localmente (sem Docker)

```bash
# 1. testa a vetorização contra os exemplos da documentação
dotnet run -c Release --project src/Rinha.IndexBuilder -- selftest

# 2. constrói o índice (precisa das referências da Rinha) — v5, int16 escala 10000
./scripts/build-index.sh ../rinha-de-backend-2026/resources/references.json.gz

# 3. mede FP/FN e latência (varre nprobe fixo e busca adaptativa)
dotnet run -c Release --project tools/Rinha.Validate -- \
    artifacts/index.bin ../rinha-de-backend-2026/test/test-data.json \
    --nprobe 16 --adaptive 8,128

# 4. sobe a API (event loop epoll) e testa via HTTP
INDEX_PATH=$PWD/artifacts/index.bin RUNTIME=epoll \
    dotnet run -c Release --project src/Rinha.Api
```

## Construir e publicar a imagem (submissão)

```bash
cp ../rinha-de-backend-2026/resources/references.json.gz resources/
export API_IMAGE=seu-usuario/rinha-2026-dotnet:latest
./scripts/build-and-push.sh   # AOT + index.bin embutido na imagem
```

## Rodar local com Docker

```bash
docker compose up -d        # nginx L4 + 2× API epoll (cpuset/mlock/UDS)
curl localhost:9999/ready
```

## Submissão

- Branch `main`: este código-fonte. Branch `submission`: só `docker-compose.yml` +
  `nginx.conf` (referenciando a imagem por **digest**) + `info.json`.
- A imagem é fixada por **digest** no compose — a Rinha cacheia tags, o digest fura o cache.
- Abra a issue `rinha/test <id>` no repo oficial para rodar a prévia (limite de 10/dia).
