# Design Copilot 能力拆解 → 本方案对照

西门子 NX Design Copilot（2506 引入，2512/2606 演进）的核心能力与替代实现路径。

## 能力对照表

| # | Design Copilot 能力 | 本方案实现 | 状态 |
| --- | --- | --- | --- |
| 1 | 自然语言理解设计意图 | DSH 内 DeepSeek 大模型（可私有化部署） | ✅ v1 |
| 2 | 模型问答（这是什么/多少个体/尺寸） | `model.tree` + `measure.distance` + 上下文对话 | ✅ v1 |
| 3 | 创建基础特征（块/圆柱等） | `feature.block` / `feature.cylinder` / `feature.sphere`（带 Undo） | ✅ v1 |
| 4 | 复杂特征（孔/布尔/草图/阵列） | 大模型生成 NXOpen 代码 → `journal.run` 编译执行，错误回传自纠错 | ✅ v1（通道）+ 模板见 `docs/nx-open-notes.md` |
| 5 | 在设计环境中定位（视图/选择） | 规划：`view.fit`、`selection.pick` 内置方法 | 🚧 路线图 |
| 6 | 设计上下文感知（关联模型状态） | `session.info` / `model.tree` 每次操作前自动读取，上下文随对话携带 | ✅ v1 |
| 7 | 装配/多部件操作 | 规划：`part.open` 已支持切换部件；装配遍历走 journal.run | 🚧 路线图 |
| 8 | 仿真/分析引导 | 规划：分析操作员 + journal（NX 仿真 API） | 🚧 路线图 |
| 9 | 数据不出网 | ✅ 本方案默认局域网，可完全离线（DeepSeek 本地模型） | ✅ 架构保证 |

## 与官方方案的本质差异

| 维度 | 西门子 Design Copilot | 本方案 |
| --- | --- | --- |
| 推理位置 | 云端（Industrial Copilot / Azure OpenAI） | DSH 侧 DeepSeek（API 或本地模型） |
| 数据流向 | 设计数据 → 西门子云 | 仅局域网 NX ↔ DSH |
| 许可 | 额外订阅（按席位） | NX 既有许可 + DSH + 模型推理成本 |
| 可控性 | 黑盒，能力固定 | 全开源：方法表、代码生成准则、安全策略都可改 |
| 成熟度 | 官方打磨，支持面广 | v1 原型：基础能力已验证，复杂能力依赖代码生成质量 |

## 路线图

1. **v1（本仓库）**：基础建模 + 模型问答 + journal 执行闭环 ✅
2. **v1.5**：`view.fit` / `selection.pick` / `feature.hole`（布尔）内置；桥端事件推送
3. **v2**：草图生成（sketch builder 模板库）、装配导航、批量部件操作
4. **v3**：错误自纠错强化（把 NX 执行日志喂回模型）、常用结构件库（法兰/支架/加强筋模板）
5. **安全加固**：方法白名单、journal.run 人工确认模式、审计日志

## 参照来源

- [Siemens brings AI copilot to NX（NX 2506，2025 夏）](https://news.siemens.com/th-th/siemens-designcenter-nx-summer-2025/)
- [AI enabled design – What's new in Designcenter NX December 2025](https://blogs.sw.siemens.com/designcenter/ai-enabled-design-whats-new-in-designcenter-nx-december-2025-release/)
- [NX 2606 (June 2026) What's new（Manufacturing）](https://blogs.sw.siemens.com/nx-manufacturing/whats-new-in-nx-for-manufacturing-2606-june-2026/)
