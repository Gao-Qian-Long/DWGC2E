# 网页与 Windows APP 支付可靠性实施记录

日期：2026-09-15。范围：单次购买、付款结算与账户权益一致性。不是完整财务系统上线声明。

## 当前结论

- 服务端修复已部署，0008 增量迁移已应用；新下单保持关闭，已有订单查询与可信回调结算不受开关影响。
- 网页购买逻辑与 Windows 原生购买窗口已实现，自动化和受控界面验证见 PAYMENT_ACCEPTANCE_20260915.md。
- **真实支付四条链路、真实续购、实体扫码、24 小时观察尚未验收；不得全量开启，不替换公开 APP 下载为未经验收的候选包。**
- 保持 EZFPY、现有 19/29 分套餐、7 天 Pro 及原额度规则。不新增支付服务商，不实现退款、自动续费或完整对账后台。

## 订单状态（兼容旧字段）

三个维度必须分开读取：

| 维度 | 字段 | 含义 |
|---|---|---|
| 创建进度 | createState=creating / unknown / ready | 正在创建／结果无法确认／二维码信息就绪；不是到账判断 |
| 付款结果 | status=pending / expired / paid 等旧状态 | pending、expired 均可接收可信迟到支付；只有服务端结算可置 paid |
| 展示期限 | expiresAt + allowedActions.pay | 只控制扫码入口展示，不代表平台已关闭订单 |

新增 displayState：paid=已付款，awaiting_payment=可以展示二维码，confirming=等待确认，其他终态保留原值。
新增 allowedActions.pay / confirm，pollAfterMs（待确认 3000ms，终态 0）。价格和权益以订单服务端快照为准。
隐藏只写 payment_order_hidden；幂等记录不删除；隐藏后已付款订单重新出现在列表。

## 服务端一致性

1. 同一用户、幂等键唯一。键绑定套餐与通道；不同内容返回 409。停购后仍重放旧意图。
2. 请求平台超时、缺字段、二维码持久化失败等保留原订单与诊断原因，不以超时判断未付款。
3. 合法回调不依赖二维码已保存。验证签名、商户、通道、金额及交易号后，通过一次结算 INSERT 和数据库触发器原子完成交易绑定、订单 paid、会员延期、额度更新、审计。
4. 事务任何一步失败均回滚；回调返回错误以便重试。重复回调核对既有结算；冲突不能再次延期。
5. 建单响应晚于回调：不覆盖 paid，只接受与已结算交易及金额一致的迟到创建结果。
6. usage_monthly 已用量不清零；有效期内续购在原到期日增加 7 天；已到期按结算时间起算。
7. 原始回调 URL、签名、密钥、支付二维码不进入诊断日志。订单号及固定原因码用于关联。

## 恢复与能力边界

payment_recovery 为持久化任务表。新单 5 分钟后进入跟踪；历史 pending/expired 增量回填；每分钟调度，每轮最多 50 单，租约 60 秒，指数退避至 1 小时。抢锁同时核对 next_attempt_at，防止两个调度器重复处理已退避的任务。

**当前没有验证通过的商户认证查单接口。任务只等待可信回调、记录重试与转人工，绝不通过未认证 findorder 自动结算。** 24 小时仍未确认转 manual，不认定未付款。客户端确认仅提前排队，不直接请求付款成功。

异常处理：恢复清单列出超过 5 分钟未确认订单，并提供最近回调拒绝记录；按订单可分页查询完整事件及人工核实记录。完整运营后台、通知推送、自动退款在本期之外。

## 接口

保留 /v1/billing/plans、checkout、orders、orders/:no 及旧返回字段。

| 接口 | 权限与行为 |
|---|---|
| GET /v1/billing/entitlements | 当前用户，一条联表查询同时返回会员、到期、当月已用／上限与下次重置时间 |
| POST /v1/billing/orders/:no/confirm | 仅订单所属用户，5 次/分钟；仅推进恢复，不信任客户端 paid 字段 |
| GET /v1/admin/billing/recovery | 独立强 ADMIN_API_KEY；未确认任务与最近冲突事件 |
| GET /v1/admin/billing/orders/:no/review?before=事件ID | 独立管理员凭证；订单和脱敏事件历史，每页 100 条 |
| POST /v1/admin/billing/orders/:no/review | operator、evidenceRef、outcome；只写审计，不强制到账 |

PAYMENTS_TEST_USERS 精确匹配用户 ID，不能使用邮箱。PAYMENTS_ENABLED=false 仅停止新购买；回调、读取、原意图重放和结算仍开放。验收账号不自动创建，不自动加入白名单。

## 双端

网页：按用户保存购买意图，不保存令牌；Web Locks 协调多标签页；页面生命周期绑定同一会话；请求代次阻止迟到响应覆盖当前订单；正常 3 秒、失败最多 30 秒轮询；隐藏暂停、重新可见／网络恢复刷新；二维码独立计时到期，不依赖下次成功查单。付款和账户同步失败分别提示。

Windows：IBillingClient 独立契约，沿用现有认证与取消机制；账户区打开原生套餐／微信支付宝二维码／详情／订单历史窗口。当前账户意图原子落盘，以文件锁防止多个 APP 进程并发创建。重启恢复原意图；退出、关闭窗口、换账号取消轮询并拒绝迟到结果。付款、账户页进入及重新激活刷新一致快照；直连模式禁购。

注意：APP 账户文件夹下 payments 仅保存套餐、通道、幂等键、订单号；不存令牌和二维码。默认 LocalAppData/DwgTranslator/payments，测试可用 DWGC2E_DATA_DIR 隔离。

## 关键源文件

- cf-worker/src/payments/index.ts：订单、回调、快照与确认接口。
- cf-worker/src/payments/recovery.ts：任务和人工审计接口。
- cf-worker/migrations/0008_payment_reliability.sql：增量任务表与结算守卫更新。
- src/DwgTranslator.Core/Api/BillingContracts.cs、WorkerApiClient.Billing.cs。
- src/DwgTranslator.App/Views/BillingWindow.xaml / .xaml.cs。
- 网站独立仓库 D:/DWGC2E_Website/js/billing.js、js/api-client.js。

本轮发布、验收结果以 PAYMENT_ACCEPTANCE_20260915.md 为准；人工核实、发布及回滚见 PAYMENT_OPERATIONS_20260915.md。


## APP 异常成功响应防护（2026-09-15 16:16）

- HTTP 200 不等于响应有效：订单、套餐、权益快照要求关键字段存在；拒绝空对象、null 列表、缺失金额/额度、非正支付金额及未知状态。
- 订单详情与确认响应必须匹配请求订单号；下单响应必须匹配原套餐和支付通道。拒绝异常响应时不让客户端获得可显示的订单对象，原幂等键由现有购买流程保留。
- 仍兼容额外字段；不改变旧 APP 所使用的服务端接口。新 APP 对不完整响应明确要求刷新原订单，不提示重复购买。
- Core 120 项及 Windows UI smoke 通过；候选包更新为 20260915-161545，未公开发行。
