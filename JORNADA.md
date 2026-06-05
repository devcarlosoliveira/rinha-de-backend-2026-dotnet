# A jornada — tentativas, erros e aprendizados

Registro honesto de como esta submissão evoluiu de **3688** para uma aposta de **pódio**,
incluindo os becos sem saída e as conclusões erradas pelo caminho. Mais útil que a versão
"deu tudo certo": aqui está o que *não* funcionou e por quê.

## O modelo de pontuação (o que estamos otimizando)

```
final = p99Score + detScore        (cada um −3000…+3000; teto 6000)
p99Score = 1000·log10(1000 / max(p99_ms, 1))      → p99 ≤ 1ms = +3000 (satura)
detScore = 1000·log10(1/ε) − 300·log10(1+E)        → E=0 = +3000
           ε = E/N,  E = 1·FP + 3·FN + 5·Err
```

Duas verdades que governaram tudo:
1. **O score satura em 6000.** Entre soluções boas, a disputa é **só p99** (todos cravam detecção).
2. **FN pesa 3×, erro HTTP 5×.** Qualquer não-200 é catastrófico (corte de 15% → −3000).

## A progressão (prévias oficiais)

| # | mudança | detScore | p99 | **final** |
|---|---|---|---|---|
| baseline | Kestrel, índice int8 | 1370 | 66 ms | 2549 |
| #8697 | **int16 escala 8000** (corrige quantização int8) | 2586 | 79 ms | 3689 |
| #8861 | servidor de **socket cru** (sem Kestrel) | 2586 | 66 ms | 3767 |
| #8884 | **busca adaptativa** (recall + menos CPU) | 2790 | 65 ms | 3975 |
| #8949 | **escala 10000** → detecção perfeita | **3000** | 59 ms | 4226 |
| #8958 | **anti-throttle** (L4 + gcConcurrent=0 + 1 CPU) | 3000 | **35 ms** | **4449** |
| #8978–93 | reescrita Rust sem-GC | 3000 | 47–78 ms | 4104–4321 |
| final | **epoll + cpuset + mlock + UDS** (não validado) | 3000 | ? | aposta |

## Fase 1 — Detecção: de "modelo-limitado" a E=0

Achávamos que a detecção estava num teto de ~1370 ("modelo-limitado"). **Errado.**

- **int8 era a fonte de erro.** Diagnóstico offline: k-NN float exato = 0 erros; int8 ≈ 190.
  Trocar para **int16** recuperou a precisão (2586).
- **Busca adaptativa.** Os erros restantes eram *near-boundary* (viram a decisão). Em vez de
  nprobe fixo, escaneia `nLow=8` buckets e **escala até `nHigh=128` só quando o resultado não
  é unânime** (fc∉{0,5}). Recupera os erros de recall **e** corta CPU média (~12,5 buckets/req).
  detScore 2586→2790, a ~10× menos custo que um nprobe=128 fixo.
- **Os 2 erros finais eram quantização int16, não empate.** Persistiam até nprobe=256.
  Subir a escala de 8000→**10000** (limite antes do overflow de int32 na soma SIMD) zerou-os:
  **E=0, detScore 3000 — teto.**

**Aprendizado:** medir antes de concluir. "Teto do modelo" era o teto do *int8*, não do problema.

## Fase 2 — A saga do p99 (a parte difícil)

A busca custa ~110 µs, mas o p99 era dezenas de ms — **700× a computação**. O gargalo nunca
foi o algoritmo; era **throttle do CFS**: sob 0.45 CPU, um burst estoura a quota de 45ms/100ms
e o container **congela ~55ms**. O p99 de ~38–66 ms é literalmente um período do CFS.

O que **funcionou** (anti-throttle, #8958, 66→35 ms):
- **LB L4** (nginx `stream`, passthrough TCP) em vez de proxy HTTP L7.
- **`DOTNET_gcConcurrent=0`** (sem thread de GC em background queimando quota).
- **`DOTNET_PROCESSOR_COUNT=1`** (.NET dimensiona threads/heaps para meia-CPU).

O que **NÃO funcionou** (becos sem saída):
- **`MAX_THREADS=2`** (#8961): serializar demais criou **fila** sob burst → 49 ms, pior.
- **`DOTNET_GCgen0size`** (#8964): 46 ms — dentro do ruído.
- **Lição do ruído:** o mesmo config rodado 2× deu 35 e 40 ms. O Mac Mini é compartilhado;
  diferenças de ~10 ms em run único são **indistinguíveis de ruído**. Não dá pra tunar fino
  com 1 amostra.

### O erro de conclusão mais caro

Concluí que **"o piso de ~38ms é da linguagem (GC) e precisaria de Rust"**. Para testar,
**reescrevi todo o runtime em Rust sem-GC** (event loop mio/epoll, AVX2, serde). Resultado:
- Paridade perfeita (E=0), mas **p99 47 ms — PIOR que o .NET** (mono-thread enfileira; multi-thread
  com SO_REUSEPORT desbalanceia).
- **Falsificou a tese:** o piso **não é GC nem linguagem** — é o throttle do CFS, agnóstico de
  linguagem. Rust não ajudou.

### A virada: analisando o rival

O usuário apontou um concorrente .NET (fksegundo) com **p99 0,35 ms (~100× melhor)**. Análise
do `docker-compose.yml` dele revelou a peça que eu tinha **ignorado a sessão inteira**:

> **`cpuset` — um core físico dedicado por API** (`api1→0`, `api2→1`, `lb→2,3`).

Com core dedicado, a quota de 0.45 roda num core **ocioso e exclusivo** → sem contenção, sem
migração → o throttle/jitter que me prendia em 38 ms **some**. Não é linguagem; é **pinar CPU**.
As peças de apoio dele (todas adotadas): event loop epoll mono-thread, **mlock** do índice
(zero page-fault), **UDS** com FD-passing, mimalloc, `network_mode: none`.

**Aprendizado:** eu cheguei perto em pedaços (socket cru, anti-throttle, UDS), mas faltava o
centro de gravidade. E fui **injusto** ao supor que os líderes "tiveram sorte ou precomputaram"
— foi engenharia de sistemas limpa e generalizável.

### io_uring: o passo além que não existe

Cogitei io_uring (menos syscalls que epoll) como diferencial. **Teste de 30s salvou horas:**
io_uring está **bloqueado pelo seccomp padrão do Docker** (`EPERM` no container). É por isso que
*todos* usam epoll. Construir io_uring teria falhado no Mac Mini → −3000.

**Aprendizado:** checar viabilidade no ambiente real **antes** de construir.

## Fase 3 — A submissão final (tudo ou nada)

Servidor **epoll mono-thread escrito do zero em .NET AOT** (P/Invoke cru de epoll/socket/mlock —
`src/Rinha.Api/Epoll/`), **meu IVF** (E=0), com **cpuset + mlock + UDS**.

Validado **localmente**: corretude E=0 nos 54.100 na stack endurecida completa, 0 erros,
concorrência e bordas OK. Descoberta de bônus: **mlock quebra o servidor async** (99,7% de falhas
— `MCL_FUTURE` trava toda alocação), mas o **epoll de baixa-alocação convive perfeitamente** —
outra razão de o event loop ser pré-requisito.

**Não** validável localmente: a *magnitude* do p99. Meu ambiente de dev tem 2 cores; o ganho do
cpuset (cores dedicados) só aparece em ≥4 (Mac Mini). E o limite de 10 prévias/dia estourou. É
uma aposta **bem-fundamentada** (mesma arquitetura do fksegundo, comprovada ~6000), com o **4449
seguro** recuperável como rede.

## Aprendizados destilados

1. **Meça antes de concluir.** "Teto do modelo" (1370) era o teto do int8. "Piso da linguagem"
   (38 ms) era throttle do CFS.
2. **O gargalo raramente é onde você acha.** A busca era µs; o p99 era agendamento de SO.
3. **`cpuset` (core dedicado) é a alavanca de p99 sob CPU fracionária** — mais que linguagem,
   GC ou transporte. Foi o que eu não enxerguei.
4. **Teste viabilidade no ambiente real cedo** (io_uring bloqueado; mlock + async incompatível).
5. **Ruído de hardware compartilhado mata tuning fino.** Sem múltiplas amostras, ~10 ms é ruído.
6. **Estude os rivais.** A peça que faltava estava num `docker-compose.yml` público.
7. **Otimização tem um teto físico.** O score satura em 6000; sub-ms é um ótimo convergente
   (epoll+pin+mlock+UDS), não há "transporte mágico" — io_uring, o único passo além, é proibido.

## O que ficou em aberto

- A magnitude do p99 da config epoll+cpuset no Mac Mini (não validável pré-prazo).
- Se uma vaga de prévia abrir antes do prazo, **um run** converteria a aposta em certeza
  (ou mandaria reverter pro 4449 a tempo).
