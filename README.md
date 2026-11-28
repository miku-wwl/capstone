# MSE907 Capstone: Financial Transaction Safety

本项目比较 PostgreSQL 金融转账 API 中两种应用层机制：乐观并发控制（OCC）和持久化请求幂等。研究使用合成账户、真实 PostgreSQL 事务及受控故障，不接触真实资金或用户。

**正式研究题目：** *Evaluating Data Consistency and Performance Trade-offs in Financial Transaction Systems under Concurrent and Retry-Prone Workloads*

## 研究问题

- **RQ1 — Correctness：** 在共享的 PostgreSQL `READ COMMITTED` 事务与业务约束下，并发、重复投递及指定故障轨迹会产生哪些不变量违例、重复财务效果或客户端与账本不一致？
- **RQ2 — Performance：** 在预定义的低、高竞争负载下，版本校验与持久化请求幂等分别及组合后，怎样影响正确确认逻辑吞吐、端到端 P95 延迟和事务成本？
- **RQ3 — Scenario interaction：** 在匹配的背景负载、热点、重复比例、种子策略、重试预算和测量窗口下，同键并发重发（W2）与提交后 HTTP 响应丢失（W4）的 OCC × 幂等交互有何差异？

## 系统与实验设计

ASP.NET Core API 将请求交给 PostgreSQL 17.7。四组共享安全的相对余额更新、稳定行锁顺序、借贷平衡与数据库约束。实验只切换两个应用层因素：

| 组 | 应用层版本校验 | 持久化请求键 |
|---|---|---|
| A | 关 | 关 |
| B | 开 | 关 |
| C | 关 | 开 |
| D | 开 | 开 |

实验运行器负责负载、故障、区组顺序、恢复点和运行证据；独立账本审计器复核余额、转账效果、分录和回执。研究使用匹配区组与区组级推断，避免把同一运行内的请求当成独立样本。

## 主要发现

- **RQ1：** 在所测 W2/W4 暴露范围内，A/B 观察到 56,532 个重复意图及额外财务效果，C/D 观察到 0 个；四组共同财务不变量违例均为 0。数据库事务正确性不能代替请求级幂等。
- **RQ2：** 在本研究的固定 120 requests/s 负载下，W1-high 中 OCC 的正确确认 P95 主效应为 +4.149 ms，持久化请求键为 +2.385 ms。该设计没有测量最大可持续吞吐或饱和点。
- **RQ3：** 预注册的 W4−W2 交互差为 +0.041667 requests/s（95% CI [+0.020450, +0.062883]）。区间满足事前估计精度要求且不含零，但绝对量约为每 60 秒 2.5 个正确确认请求，工程幅度很小。

结论限于该合成 API、单机 PostgreSQL、固定负载与已实施故障；不能外推为银行生产系统性能或风险证明。

## 快速开始

在 Windows PowerShell 中，先安装 .NET 10 SDK 与 Docker Desktop，再从仓库根目录运行：

```powershell
./infrastructure/Enter-Lab.ps1
dotnet restore Capstone.slnx --locked-mode
dotnet build Capstone.slnx --no-restore -c Release
dotnet test --solution Capstone.slnx --no-build -c Release
```

若要启动本地合成实验 API，在专用实验数据库中迁移并重置账户，然后选择一个组：

```powershell
dotnet run --project src/Capstone.Api --no-build -c Release -- --migrate
dotnet run --project src/Capstone.Api --no-build -c Release -- --reset 907 1000
$env:CAPSTONE_GROUP = 'A'  # A、B、C 或 D
dotnet run --project src/Capstone.Api --no-build -c Release
```

API 默认监听 `http://127.0.0.1:5187`；`GET /healthz` 显示当前组。只在本地合成数据库中操作。

## 学习入口

- [中文学习指南](docs/LEARNING_GUIDE.md)：按研究基础、交易工程、正确性、实验方法、统计分析和答辩安排阅读。
- [Assessment 1 v1.2](docs/week02/v1.2/)：当前研究计划与原始冻结协议。
- [Assessment 2 WIP](docs/week03/Day07/)：保留在 Pilot 阶段的阶段性研究材料。
- [Assessment 3 Final Research Report](docs/week04/Day05/) 与 [答辩材料](docs/week04/Day06/)。
- [Week01–Week04 研究档案](docs/)：逐日材料与历史周报；旧版本保留用于版本追溯。

## 复现已发布分析

冻结协议及随机区组清单在 [Week04 Day01](docs/week04/Day01/)。正式原始数据分为 [Day02 第一批](docs/week04/Day02/) 和 [Day03 第二批](docs/week04/Day03/)；对应 ZIP 与清单保存在项目 [Google Drive Week04](https://drive.google.com/drive/folders/1fgFSHVdZxBYM9AuKnHGPqAoLGXwHffq7)。Pilot 的六份原始归档（含第4区组首次失败尝试）保存在 [Google Drive Week03 Day06](https://drive.google.com/drive/folders/1qLnIepP_o1XSxkPHq_uoR8nMi1pG7l52)。原始 ZIP 不是日常阅读的必需材料。

将所需归档下载到本地忽略目录后，可按以下步骤恢复和复算。为每次运行选新的输出路径；归档与数据不可混合 Pilot 和 Formal。

```powershell
dotnet restore Capstone.slnx --locked-mode
dotnet build Capstone.slnx --no-restore -c Release
python -m pip install -r experiments/analysis/requirements-figures.txt scipy
python experiments/analysis/restore_evidence.py _local_sources/formal-repro docs/week04/Day02/Formal_Batch1_Manifest.json docs/week04/Day03/Formal_Dataset_Manifest.json --archives _local_sources/downloaded-formal
python experiments/analysis/restore_evidence.py _local_sources/pilot-repro docs/week03/Day06/Pilot_Precision.json --archives _local_sources/downloaded-pilot
dotnet run --project experiments/Capstone.Experiments --no-build -c Release -- verify _local_sources/formal-repro
python experiments/analysis/analyse_study.py formal _local_sources/formal-repro _local_sources/recomputed.json --freeze docs/week04/Day01/01_实验配置与随机区组清单.json
python experiments/analysis/audit_study_independent.py _local_sources/pilot-repro _local_sources/formal-repro docs/week04/Day01/01_实验配置与随机区组清单.json docs/week04/Day04/Analysis_Formal.json _local_sources/independent-audit.json
python experiments/analysis/analyse_interaction_mechanism.py _local_sources/formal-repro docs/week04/Day01/01_实验配置与随机区组清单.json docs/week04/Day04/Analysis_Formal.json _local_sources/interaction-mechanism.json
python experiments/analysis/plot_study.py _local_sources/recomputed.json _local_sources/recomputed-figures
python -m unittest discover -s experiments/analysis -p 'test_interaction_mechanism.py'
```

运行独立核验命令前，也需将 Pilot 六份归档恢复至 `_local_sources/pilot-repro`。归档清单、哈希和恢复约束见 [Day02](docs/week04/Day02/)、[Day03](docs/week04/Day03/) 与 [Day04 统计分析](docs/week04/Day04/)。

## 研究材料

代码入口：`src/Capstone.Api/`（API 与准入）、`src/Capstone.Postgres/`（事务、持久化键、账本审计）、`tests/`（单元与 PostgreSQL 集成测试）、`experiments/Capstone.Experiments/`（Pilot 与 Formal 运行器）、`experiments/analysis/`（恢复、独立审计、统计及图表）。GitHub Actions 在每次推送时运行锁定依赖构建、测试和实验计划校验。

文档入口： [正式实验协议](docs/week04/Day01/01_正式实验协议_CN.txt)、[统计结果](docs/week04/Day04/04_统计分析与研究发现_CN.txt)、[Assessment 3 英文报告](docs/week04/Day05/Assessment3_Final_Research_Report_EN.docx)、[中文审阅版](docs/week04/Day05/Assessment3_最终研究报告_CN.docx)、[英文演示](docs/week04/Day06/Assessment3_Final_Presentation_EN.pptx)、[中文答辩提纲](docs/week04/Day06/Assessment3_答辩与录制提纲_CN.txt)。

## 复现边界与学生待办

原始研究数值、种子和冻结配置由 Git 历史、Manifest 与云端原始归档共同追溯。统计复算不需要重新运行 80 轮 Pilot 或 160 轮 Formal。固定到达率没有估计饱和容量，T12 没有模拟真实 PostgreSQL 网络层的 COMMIT ACK 丢失，结果也未涵盖多节点生产环境。

报告和演示是学生审阅材料。学生仍需亲自理解和核对引用、按学校要求完成自己的学术表达与 AI 使用披露、填写身份信息、录制答辩并正式提交。
