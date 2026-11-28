# Capstone 学习指南

这份指南是一张阅读地图，不复制 Proposal、研究报告或原始数据。建议沿着 **Why → What → How → Evidence → Trade-offs → Engineering implications** 阅读：先明确问题，再理解机制，最后用真实研究证据判断适用范围。

## 建议顺序

1. 研究基础：从文献和研究空白走到可测量的问题及实验协议。
2. 交易工程：先掌握共同安全基线，再学习 OCC 与持久化幂等。
3. 正确性与故障：用测试和独立账本审计区分数据库不变量与请求语义。
4. 实验方法：读 Pilot，再读冻结协议和 Formal 执行记录。
5. 统计分析：理解实验单位、区组对比、效应量和区间。
6. 研究发现与答辩：对照真实结果，练习解释机制、成本与限制。

逐日记录与周报保留了研究当时的进展状态。日常学习优先阅读下方的主题材料；JSON、ZIP、Manifest 和逐请求轨迹是有需要时才打开的证据入口。

## Part 1 — Research Foundations

**学习目标：** 从研究方向和文献证据，推导出明确的 gap、可测量的 RQ 和可执行的实验设计。

**核心知识：** Literature Review 与批判性综合、Research Gap、RQ 可测量性、2×2 Factorial Design、主要/次要比较、研究范围与效度边界。

**推荐阅读：** [Week01 论文线索](week01/Capstone_18_Paper_Links.txt) → [v1.2 核心论文链接](week02/v1.2/Day01/01_核心论文链接.txt) → [批判性文献综述](week02/v1.2/Day02/02_批判性文献综述_CN.txt) → [研究空白与 RQ](week02/v1.2/Day03/03_研究空白与研究问题_CN.txt) → [实验协议与计划](week02/v1.2/Day04/04_实验协议与实施计划_CN.txt) → [Assessment 1 Proposal](week02/v1.2/Day05/Assessment1_Research_Proposal_v1.2_EN.docx) → [Week03 系统设计与方法](week03/Day01/01_系统设计与研究方法_CN.txt)。

**对应源码/实验：** 本阶段形成的设计由 [四组代码与测试](../src/Capstone.Postgres/) 和 [Formal 冻结协议](week04/Day01/01_实验配置与随机区组清单.json) 实现；先读文档，再回看实现。

**自测问题：** RQ1、RQ2、RQ3 各自的观测单位和指标是什么？为什么 W2/W4 的 RQ3 需要同区组配对？为什么实验贡献是边界明确的经验比较，而不是普遍性能结论？

## Part 2 — Financial Transaction Engineering

**学习目标：** 解释安全数据库事务、应用层版本校验和持久化请求幂等各自保护什么。

**核心知识：** PostgreSQL `READ COMMITTED`、安全相对更新、行锁与稳定更新顺序、OCC 版本谓词和有界重试、请求键唯一协调、payload fingerprint、原回执恢复、Savepoint、Unknown COMMIT Outcome。

**推荐阅读：** [Week03 Day02 安全基线](week03/Day02/02_安全基线与账本验证_CN.txt) → [Day03 OCC](week03/Day03/03_OCC实现与验证_CN.txt) → [Day04 幂等与恢复](week03/Day04/04_幂等与事务恢复验证_CN.txt) → 查看下方实现。

**对应源码/实验：** [TransferService](../src/Capstone.Postgres/TransferService.cs)、[DurableKeys](../src/Capstone.Postgres/DurableKeys.cs)、[API 准入](../src/Capstone.Api/LabAdmission.cs)、[安全基线集成测试](../tests/Capstone.IntegrationTests/SafeBaselineTests.cs)、[版本校验测试](../tests/Capstone.IntegrationTests/VersionValidationTests.cs)、[幂等恢复测试](../tests/Capstone.IntegrationTests/IdempotencyRecoveryTests.cs)。

**自测问题：** 为什么 ACID 事务不能保证客户端重试只产生一次业务效果？OCC 与幂等分别阻止什么？持久回执记录解决了哪些“提交成功但响应未知”的恢复问题？

## Part 3 — Correctness and Fault Injection

**学习目标：** 通过独立判据区分数据库事务不变量、请求级效果、客户端确认和故障后的恢复。

**核心知识：** 资金守恒、非负余额、借贷平衡、原子性、每个 logical request 的财务效果数、回执与账本对账、T1–T13、W0–W5、注入故障与真实传输故障的边界。

**推荐阅读：** [Week03 Day05 正确性与故障注入](week03/Day05/05_正确性与故障注入验收_CN.txt) → [独立账本审计器](../src/Capstone.Postgres/LedgerAuditor.cs) → 测试文件。

**对应源码/实验：** [正确性门测试](../tests/Capstone.IntegrationTests/CorrectnessGateTests.cs)、[账本审计器](../src/Capstone.Postgres/LedgerAuditor.cs)、[Lab API 故障路径](../src/Capstone.Api/LabApi.cs)。

**自测问题：** 为什么 HTTP 200 不等于正确财务效果？为什么“所有账本都能对平”仍可能有重复扣款？T12 的应用层注入无法证明什么？

## Part 4 — Experimental Methodology

**学习目标：** 从 Pilot 检查运行可行性，到冻结、执行和审计 Formal 实验。

**核心知识：** 固定开放式负载、匹配随机区组、种子与运行顺序、Warm-up/Measurement/Drain、Reset、配置冻结、Checkpoint、Invalid/Rerun 规则、Pilot 与 Formal 数据隔离。

**推荐阅读：** [Week03 Day06 Pilot](week03/Day06/06_Pilot实验与精度评估_CN.txt) → [Week04 Day01 冻结协议](week04/Day01/01_正式实验协议_CN.txt) → [Day02 第一批记录](week04/Day02/02_正式实验第一批记录_CN.txt) → [Day03 数据质量验收](week04/Day03/03_正式实验与数据质量验收_CN.txt)。

**对应源码/实验：** [实验运行器](../experiments/Capstone.Experiments/ExperimentRunner.cs)、[工作负载计划](../experiments/Capstone.Experiments/WorkloadPlan.cs)、[正式冻结校验](../experiments/Capstone.Experiments/FormalFreeze.cs)、[Checkpoint](../experiments/Capstone.Experiments/StudyCheckpoint.cs)、[归档恢复工具](../experiments/analysis/restore_evidence.py)。

**可选原始证据：** [Pilot 归档（6 份，含首次失败记录）](https://drive.google.com/drive/folders/1qLnIepP_o1XSxkPHq_uoR8nMi1pG7l52)、[Formal 第一批](https://drive.google.com/drive/folders/1w1XuIQO0DRtU8Ynp45Spmx2jFi0_a_d7)、[Formal 第二批](https://drive.google.com/drive/folders/1VuwXjSSiWbg5iRDwK-W3I--RyOAtRgsb)。这些是深度复现材料，不是入门阅读要求。

**自测问题：** 为什么运行/区组才是统计重复单位？什么情况下应判整组无效并重跑？随机顺序与 workload seed 为什么分开？

## Part 5 — Statistical Analysis

**学习目标：** 能从冻结口径和原始数据复算指标，正确区分统计证据、精度和工程意义。

**核心知识：** Offered/Dispatched/Correctly Confirmed、P95 条件样本、2×2 主效应和交互、区组级对比、Student-t 95% CI、估计精度与 practical significance、事后机制分析的因果边界。

**推荐阅读：** [Week04 Day04 统计结果与解释](week04/Day04/04_统计分析与研究发现_CN.txt) → 阅读 [冻结分析 JSON](week04/Day04/Analysis_Formal.json) 与 [RQ3 机制 JSON](week04/Day04/RQ3_Mechanism_Analysis.json) 中相关结果 → 运行分析脚本。

**对应源码/实验：** [正式统计分析](../experiments/analysis/analyse_study.py)、[独立复核](../experiments/analysis/audit_study_independent.py)、[RQ3 匹配机制分析](../experiments/analysis/analyse_interaction_mechanism.py)、[图表生成](../experiments/analysis/plot_study.py)、[机制分析测试](../experiments/analysis/test_interaction_mechanism.py)。

**自测问题：** 为什么一百多万个请求不是一百多万个独立样本？RQ3 区间不含零说明什么、又不说明什么？为什么满足 δ 只代表达到估计精度要求？

## Part 6 — Research Findings and Defence

**学习目标：** 将文献、方法、实现和真实结果组成可辩护的工程结论，并说明外推限制。

**核心知识：** Literature-to-finding synthesis、结果与机制的区分、统计与实际意义、伦理文化影响、Threats to Validity、工程决策边界与答辩。

**推荐阅读：** [Assessment 2 英文 WIP](week03/Day07/Assessment2_WIP_Report_EN.docx) / [中文 WIP](week03/Day07/Assessment2_阶段性研究报告_CN.docx) → [Assessment 3 英文报告](week04/Day05/Assessment3_Final_Research_Report_EN.docx) / [中文报告](week04/Day05/Assessment3_最终研究报告_CN.docx) → [英文 Presentation](week04/Day06/Assessment3_Final_Presentation_EN.pptx) → [中文答辩与录制提纲](week04/Day06/Assessment3_答辩与录制提纲_CN.txt) → [最终提交前清单](week04/Day07/Assessment3_最终验收与提交前清单_CN.txt)。

**对应证据：** [Week04 统计图表](week04/Day04/) 与三项研究问题对应；[Week03 伦理与文化分析](week03/Day01/01_伦理与文化分析_CN.txt) 讨论的是潜在工程影响，不是本项目的真实用户测量。

**自测问题：** 什么场景需要持久请求幂等？什么时候版本校验值得额外延迟？RQ3 的小绝对差异怎样影响工程决策？哪些结论不能外推到真实银行生产环境？

## 复现入口

平时按六部分阅读即可。需要复算时，从 [README 的复现步骤](../README.md#复现已发布分析) 开始，使用 [冻结配置](week04/Day01/01_实验配置与随机区组清单.json)、原始归档 Manifest、`restore_evidence.py` 和分析脚本。不要改动原始 ZIP、随机种子或冻结统计定义；完整复现不需要重跑 Pilot 或 Formal。
