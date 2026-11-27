# Capstone — MSE907

**Project:** Evaluating Data Consistency and Performance Trade-offs in Financial Transaction Systems under Concurrent and Retry-Prone Workloads

Yoobee Master of Software Engineering · Industry-based Capstone Research Project

## Assessment 1 v1.2 — Refined Research Proposal

The ten Assessment 1 proposal materials are in [docs/week02/v1.2/](docs/week02/v1.2/). Assessment 1 v1.0 and v1.1 are historical versions.

The proposal compares application-level OCC and persistent request idempotency in a synthetic transfer API with a shared PostgreSQL READ COMMITTED baseline.

## Assessment 2 prototype and validation

The loopback prototype implements groups A–D. [Week 03](docs/week03/) records the implementation, transaction tests and engineering smoke. The study pilot and formal experiments remain pending institutional research clearance and protocol freeze.

From the repository root in PowerShell, with the pinned .NET SDK and Docker available:

```powershell
./infrastructure/Enter-Lab.ps1
dotnet restore Capstone.slnx --locked-mode
dotnet build Capstone.slnx --no-restore -c Release
dotnet test --solution Capstone.slnx --no-build -c Release
dotnet run --project experiments/Capstone.Experiments --no-build -c Release -- smoke _local_sources/engineering/example
dotnet run --project experiments/Capstone.Experiments --no-build -c Release -- verify _local_sources/engineering/example
```

Use a fresh output directory for each smoke. Run it against the dedicated synthetic lab only. The optional `plan` command validates candidate settings and prints a randomized block schedule without executing it.

## Academic integrity and submission

Before formal submission, the student must verify and understand the cited sources, complete their own name and ID, revise the draft in their own words, follow institutional AI-use requirements and confirm current course dates and formats.
