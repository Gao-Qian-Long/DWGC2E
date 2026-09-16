# 云端改动语义审查补充（2026-09-16）

## 本轮完成

- 已完成订单/支付相关本地回归：76 项通过，0 失败、0 跳过；覆盖签名、下单幂等、回调竞争、回滚、旧订单兼容、模拟 D1。
- 已补充 `cf-worker/tests/populated-migration.test.mjs`，验证已有用户、订单、订阅、用量、结算和事件数据在 0008–0010 迁移后保持不变，并验证重复结算不重复授予、冲突交易被拒绝：2 项通过。
- 先前云端基础范围测试：72 项通过；TypeScript 类型检查通过。

## 审查结论

这些改动包含真实业务语义变化，不属于单纯目录整理：术语库上限/版本校验、历史元数据、请求体限制、代理客户端地址和限流、留言后台管理、支付恢复与结算保护、分钟级 Worker 调度、管理员会员审计等。因此本报告不把它们标记为“无行为变化”。

迁移测试证明空库和本地填充数据场景可通过，但不等同于线上 D1 迁移证明；没有连接生产数据库，也没有部署 Worker。`PAYMENTS_ENABLED` 原本就是 `true`，本轮未声称启用支付；未进行真实支付。

## 未完成/需人工验收

- 线上 D1 真实升级前的备份、影子库演练和回滚方案。
- 真实部署后的 Cron、留言后台、登录限流、代理签名链路验证。
- 真实登录、会员、CAD、翻译、输出、设置和更新回归仍按用户要求延期。
- 全工作区仍存在其他任务留下的 diff-check 空白问题，未进行无关格式化。

## Full suite follow-up
2026-09-16 ran the complete cf-worker test glob: 241 passed, 0 failed, 0 skipped. Evidence: artifacts/installer-safety-20260916-resumed/cloud-review/full-suite.log. Source hashes are recorded in source-hashes.json.

## 有数据升级后的结算补充
新增pending/expired结算及错误金额无部分写入测试；完整套件244通过、0失败/跳过。后续证据为 artifacts/installer-safety-20260916-resumed/cloud-review/full-suite-upgrade.log；此前241项和source-hashes.json为较早快照，不能代表新增测试后的文件哈希。
