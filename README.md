# Capstone — MSE907

**Project** Evaluating data consistency and performance trade-offs in financial transfers under concurrent and retry-prone workloads.

The local .NET transfer API uses PostgreSQL 17.7. The generator records per-intent and per-attempt evidence. The independent ledger auditor joins opening and closing balances, transfer effects, ledger entries and receipts.

| Group | Application version check | Persistent request key |
|---|---|---|
| A | Off | Off |
| B | On | Off |
| C | Off | On |
| D | On | On |

Every group uses safe relative writes, stable row-lock order and one READ COMMITTED transaction. The study uses generated accounts and faults on loopback.

## Build and engineering checks

Run these commands in PowerShell from the repository root with Docker and the pinned .NET SDK.

~~~powershell
./infrastructure/Enter-Lab.ps1
dotnet restore Capstone.slnx --locked-mode
dotnet build Capstone.slnx --no-restore -c Release
dotnet test --solution Capstone.slnx --no-build -c Release
dotnet run --project experiments/Capstone.Experiments --no-build -c Release -- smoke _local_sources/engineering/repro
dotnet run --project experiments/Capstone.Experiments --no-build -c Release -- verify _local_sources/engineering/repro
~~~

For manual operation in the same dedicated synthetic lab, migrate and seed the database once, then choose one of the four API groups.

~~~powershell
dotnet run --project src/Capstone.Api --no-build -c Release -- --migrate
dotnet run --project src/Capstone.Api --no-build -c Release -- --reset 907 1000
$env:CAPSTONE_GROUP = 'A'
dotnet run --project src/Capstone.Api --no-build -c Release
~~~

The API listens at `http://127.0.0.1:5187`. Query `GET /healthz` to check the active group.

## Reproduce the published analysis

The frozen plan and committed configuration are in [Week04 Day01](docs/week04/Day01/). Download both ZIP batches identified and hashed in [Day02](docs/week04/Day02/) and [Day03](docs/week04/Day03/) into one directory. The restore command checks the archive and member hashes and uses the Day03 final checkpoint. Pilot and formal trials use separate datasets and seeds.

~~~powershell
python experiments/analysis/restore_evidence.py _local_sources/formal-repro docs/week04/Day02/Formal_Batch1_Manifest.json docs/week04/Day03/Formal_Dataset_Manifest.json --archives _local_sources/downloaded-formal
dotnet run --project experiments/Capstone.Experiments --no-build -c Release -- verify _local_sources/formal-repro
python experiments/analysis/analyse_study.py formal _local_sources/formal-repro _local_sources/recomputed.json --freeze docs/week04/Day01/01_实验配置与随机区组清单.json
python -m pip install -r experiments/analysis/requirements-figures.txt
python experiments/analysis/plot_study.py _local_sources/recomputed.json _local_sources/recomputed-figures
~~~

The `formal` command takes the committed protocol, its SHA-256, the output directory and the last block for the batch. It checks the frozen executable and environment. Use the same directory to resume an existing formal dataset.

## Findings and research materials

- 160 formal runs across 10 matched blocks. Financial-invariant violation cells 0. W2/W4 duplicate intents totalled 56,532 with persistent keys off and 0 with keys on.
- W1 throughput stayed close to the 120 requests/s offered rate. In W1-high, the OCC and persistent-key effects on correctly confirmed P95 were 4.149 ms and 2.385 ms.
- The paired scenario interaction was +0.041667 [+0.020450, +0.062883] correctly confirmed requests/s. RQ3 status confirmatory.
- Measured effects apply to the registered fixed offered rate, single-host PostgreSQL and artificial response loss. Latency and retry counters qualify throughput under this load, and any population-wide claims require new evidence.
- [Assessment 1 proposal](docs/week02/v1.2/), [Assessment 2 WIP](docs/week03/Day07/), [pilot evidence](docs/week03/Day06/), [formal analysis](docs/week04/Day04/), [Assessment 3 report](docs/week04/Day05/) and [presentation](docs/week04/Day06/).
- [Week03 evidence on Drive](https://drive.google.com/drive/folders/1ZmSr6H-9-k7HM09VX3tUm_OwhrMVus5f) and [Week04 evidence on Drive](https://drive.google.com/drive/folders/1fgFSHVdZxBYM9AuKnHGPqAoLGXwHffq7).

The reports and presentation are student review copies. The student handles personal review, identification, AI disclosure, recording and formal submission.
