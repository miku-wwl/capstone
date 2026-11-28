# Capstone — MSE907

**Project** Evaluating Data Consistency and Performance Trade-offs in Financial Transaction Systems under Concurrent and Retry-Prone Workloads

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

For independent verification, download the six [Pilot archives](https://drive.google.com/drive/folders/1qLnIepP_o1XSxkPHq_uoR8nMi1pG7l52), including the first attempt at block 4, to `_local_sources/downloaded-pilot`. The independent verifier uses SciPy for Student-t quantiles. Use a new output path when repeating a command.

~~~powershell
python experiments/analysis/restore_evidence.py _local_sources/pilot-repro docs/week03/Day06/Pilot_Precision.json --archives _local_sources/downloaded-pilot
python -m pip install scipy
python experiments/analysis/audit_study_independent.py _local_sources/pilot-repro _local_sources/formal-repro docs/week04/Day01/01_实验配置与随机区组清单.json docs/week04/Day04/Analysis_Formal.json _local_sources/independent-audit.json
python experiments/analysis/analyse_interaction_mechanism.py _local_sources/formal-repro docs/week04/Day01/01_实验配置与随机区组清单.json docs/week04/Day04/Analysis_Formal.json _local_sources/interaction-mechanism.json
python -m unittest discover -s experiments/analysis -p test_interaction_mechanism.py
~~~

The [request-level audit](docs/week04/Day04/RQ3_Mechanism_Analysis.json) is a post-hoc exploratory decomposition of the W2/W4 contrast. It traces matched requests to the original HTTP attempts and ledger effects.

The `formal` command takes the committed protocol, its SHA-256, the output directory and the last block for the batch. It checks the frozen executable and environment. Use the same directory to resume an existing formal dataset.

## Findings and research materials

- 160 formal runs across 10 matched blocks. Financial-invariant violation cells 0. W2/W4 duplicate intents totalled 56,532 with persistent keys off and 0 with keys on.
- W1 throughput stayed close to the 120 requests/s offered rate. In W1-high, the OCC and persistent-key effects on correctly confirmed P95 were 4.149 ms and 2.385 ms.
- The preregistered W4−W2 interaction was +0.041667 requests/s (95% CI [+0.020450, +0.062883]). The interval met the precision rule for confirmatory status. The absolute contrast amounts to 2.5 correctly confirmed requests per 60 seconds on the interaction scale, or 0.0347% of the offered rate.
- The fixed arrival schedule supports a comparison at 120 requests/s. Saturation capacity, database-wire acknowledgement loss and production impact remain unmeasured. W2/W4 P95 covers the correctly confirmed cohort, whose membership differs across treatments.
- [Assessment 1 proposal](docs/week02/v1.2/), [Assessment 2 WIP](docs/week03/Day07/), [pilot evidence](docs/week03/Day06/), [formal analysis](docs/week04/Day04/), [Assessment 3 report](docs/week04/Day05/) and [presentation](docs/week04/Day06/).
- [Week03 evidence on Drive](https://drive.google.com/drive/folders/1ZmSr6H-9-k7HM09VX3tUm_OwhrMVus5f) and [Week04 evidence on Drive](https://drive.google.com/drive/folders/1fgFSHVdZxBYM9AuKnHGPqAoLGXwHffq7).

The reports and presentation are student review copies. The student handles personal review, identification, AI disclosure, recording and formal submission.
