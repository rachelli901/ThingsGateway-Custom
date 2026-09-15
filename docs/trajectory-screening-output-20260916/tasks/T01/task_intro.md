# 候选任务介绍

## 基本信息
- 安全任务 ID：T01
- 脱敏标题：修复高并发采集下 AsyncReadWriteLock 读者被写者抢占误取消
- 会话 ID 和安全定位符：session_id `01a0a683-f27c-7652-ae21-e20536f0d419`；`%USERPROFILE%\.codex\sessions\2026\09\16\rollout-2026-09-16T03-21-00-01a0a683-f27c-7652-ae21-e20536f0d419.jsonl`
- 技术领域和任务类型：非 GPU 通用软件工程 / 并发异步锁缺陷修复（Bug 定位 + 重构 + 回归测试）
- 最终状态：成功

## 任务定义
- 既有工程背景：ThingsGateway 网关采集子系统（C#/.NET，多目标框架 net8.0/net10.0）；`AsyncReadWriteLock` 为公共并发原语，被 `CollectBase` 等底层采集驱动广泛使用。
- 初始问题：高并发采集下频繁写请求进入时，排队或执行中的读任务抛出 `OperationCanceledException` 被无故取消，导致数据采集中断。
- 最终目标和约束：定位并修复读者被误取消的问题；把“调用方取消”与“写者抢占”分离；在保持 `ReaderLockAsync` 原有公共 API（零破坏性变更）的前提下改造；改动完成后用 `dotnet test` 验证。

## 人机协作
- 用户新增信息（如有；不要求多轮补充）：
  - 第 2 轮：要求先给出重构架构思路与伪代码，确认不影响现有无锁热路径性能后再改源码；
  - 第 3 轮：给出关键专家约束——把返回类型改成 `ReaderLease` 会造成 `CollectBase` 等数十处调用方的破坏性变更，要求保持原签名、对上层透明、零破坏性改动；
  - 第 4 轮：确认“双通道加法式”方案并要求落地实现并运行 `dotnet test`。
- Agent 关键观察、行动和调整：
  - 先跑 `dotnet test` 基线：首次因沙箱无法读取用户级 `NuGet.Config` 失败（非代码问题），提权重跑后 net8.0/net10.0 通过 1、失败 0；并指出既有测试未覆盖本异常的核心语义；
  - 定位根因：写者优先是通过“替换并取消旧共享取消令牌”实现的，等于主动取消活跃读者；
  - 受专家约束调整方案：由“直接返回 ReaderLease”改为“保留旧公共签名 + 新增内部 Lease 通道”的双通道加法式设计；
  - 实现中发现强命名程序集 `InternalsVisibleTo` 需公钥（CS1726），放弃该测试访问方案，改为不引入签名配置变更的方式；
  - 额外修复 `AsyncAutoResetEvent.SetAll` 的丢失唤醒，以及旧通道 `_readerCount` 在异常路径未回滚的问题。

## 验收条件
| 核心要求 | 可观察结果 | 验证证据 | 当前状态 |
| --- | --- | --- | --- |
| 定位写者抢占导致读者被误取消的根因 | 根因指向写者进入时替换并取消共享取消令牌 | 第 1 轮 task_complete 消息（ordinal 143） | 已满足 |
| 保持 `ReaderLockAsync` 公共 API 不变 | 公共签名仍为 `ValueTask<CancellationToken> ReaderLockAsync(CancellationToken)` | 末轮改动说明与 `git diff`（ordinal 468-469、555-556） | 已满足 |
| 实际修改源码与调用点 | `AsyncReadWriteLock.cs`、`CollectBase.cs` 已改 | 工作区 `git status --short` 显示 3 文件 modified | 已满足 |
| 新增回归测试覆盖 Lease 语义 | 覆盖 4 项 Lease 语义 | `AsyncReadWriteLockTest.cs` | 已满足 |
| `dotnet test` 通过 | net8.0/net10.0 各通过 2、失败 0 | 末轮 `dotnet test` 结果（ordinal 536-569） | 已满足 |

## 支撑材料
| 材料 | 用途 | 完备状态 | 安全定位符 |
| --- | --- | --- | --- |
| 原生会话 JSONL | 原始轨迹 | 完整 | `%USERPROFILE%\.codex\sessions\2026\09\16\rollout-2026-09-16T03-21-00-01a0a683-f27c-7652-ae21-e20536f0d419.jsonl` |
| ThingsGateway 工作区（分支 v12，基线提交 b9c92a5c） | 源码与验证入口 | 基本完整 | `%WORKSPACE_ROOT%` |
| `AsyncReadWriteLock.cs` / `CollectBase.cs` / `AsyncReadWriteLockTest.cs` | 修复实现与回归测试 | 完整 | 工作区相对路径 |
| 审批守卫子代理派生会话 | 辅助记录（同 session_id） | 完整 | `%USERPROFILE%\.codex\sessions\2026\09\16\rollout-2026-09-16T04-08-14-01a0a6af-3313-7ed3-9fac-28d31cf0cc18.jsonl` |
| 附件 | — | 无 | — |

## 评价
- Workspace 完备性：完整
- 复杂度：高
- 七个价值方向：工程价值 强 / 专业挑战 强 / Agentic 深度 强 / 专家贡献 强 / 任务闭环 强 / 环境质量 中 / 验证价值 强
- 候选层级：强候选（推荐提交）

## 结论
- 分类：推荐提交
- 缺口和风险：原生会话包含本机工作目录等绝对路径；修复以未提交工作区改动形式存在；提交前需确认权属与许可。
- 提交前动作：确认提交授权与源仓库许可；如需完全去标识，由提交侧对原生会话另行处理。
