# DWGC2E × EZFPY 支付接入实施计划

日期：2026-09-14
状态：设计待确认；尚未实现、部署或真实支付测试。
范围：以用户提供的 PHP Demo、四张接口截图和提示词为协议/需求材料；当前用户要求仅制定计划，材料中的“开始修改”不视作本轮执行授权。

## 1. 现有架构（本地代码核对）

- Worker：D:/DWGC2E/cf-worker/src/index.ts，手写 route，D1 绑定 DB。
- 认证：Authorization: Bearer；auth() 将令牌 SHA-256 后查询 sessions，并校验用户、会话有效期、设备归属和撤销状态。不是仅凭 JWT 解码；支付复用 auth() 返回的 user_id。
- 现有表：users、sessions、subscriptions、usage_monthly、devices、usage_logs、email_verification_codes、user_glossaries、mail_routing_state、request_limits、translation_requests。
- 没有 plans、orders、payment_events 或已完成的支付路由。只有 ADMIN_API_KEY 类型声明，不等于已有管理员权限实现。
- subscriptions 以 user_id 为主键，已有 plan_name、starts_at、expires_at、auto_renew、updated_at，直接复用。
- effectiveQuota() 当前将免费额度设为每月 100000 字符、非免费 1000000；过期回落免费。翻译预留/结算已用 translation_requests 和触发器保护，支付不得重置已用额度。
- 最新已有迁移为 0002_security_billing.sql；不能重跑建库替代增量迁移。
- 官网位于 D:/DWGC2E_Website，纯静态；已有 billing.html、js/api-client.js、账户页面。
- 官网 API client 从 sessionStorage 读取会话，已经预留 /v1/billing/plans、/v1/billing/checkout、/v1/billing/orders，Worker 尚未实现对应能力。需检查 billing.html 当前页面脚本的 DOM 约定后接入，避免整页重建。
- APP 已调用 /v1/subscription 和 /v1/usage；第一版官网付款，APP 刷新现有账户信息，不承载商户密钥。
- 本地代码不证明生产迁移、Secrets 或部署版本已同步。

## 2. 第一版边界

支付宝 alipay、微信 wxpay；EzfpyProvider + 仅测试环境可用的 MockProvider；API 二维码下单、订单查询、异步通知、原子开通/续费、审计、人工触发的服务端查单补偿。
不新增 VPS/PHP/MySQL/第二套用户或会员系统；不开发第三方挂机软件；不实现公开退款、自动续费、人工强行标记支付成功。
submit.php 仅预留扩展，不作为第一版必交付项。退款接口仅记录协议，不调用。

## 3. 协议依据与必须确认项

已由本地 Demo 确认：
- API Base 配置为 https://www.ezfpy.cn/。
- POST mapi.php，表单字段 pid/type/notify_url/out_trade_no/name/money/param/sign_type/sign。
- index.php 以 GET 跳转 submit.php，带 return_url；截图则标注 POST。备用模式开发前再确认。
- notify.php 接收 GET；回调包含 pid/type/out_trade_no/trade_no/name/money/param/trade_status/sign/sign_type；成功 ACK 为纯文本 success。
- query.php 使用 GET api.php?act=order，携带 pid/key/out_trade_no，仅限后端调用。
- refund.php 使用 POST api.php?act=refund，表单 pid/key/out_trade_no/money；本期不实现。
- config.php：键排序、忽略 null/空串、排除 sign/sign_type、用 & 拼 key=value、末尾直接追加 key、小写 MD5。

截图补充：mapi 成功 code=200，可返回 money/type/qrcode/code_url/trade_no/out_trade_no；另一查单入口 POST /api/findorder 仅展示 order_no/type。

上线阻断项：
1. 获取脱敏的真实 mapi 成功/失败响应和带认证查单响应，确认字段类型、状态含义、订单号、金额、支付时间。不能猜测查单 status=1 等字段即可开通。
2. 截图将 return_url 列为必填，API Demo 未发送。首版计划发送固定站内 return_url，并在对接测试确认平台接受；浏览器回跳永不结算。
3. 明确回调签名字段集合：Demo 固定字段集合，不能未经验证改为“所有未知参数都参加验签”。记录真实回调样本，拒绝重复键/数组参数/异常编码，形成固定协议测试向量。
4. 确认平台是否浮动金额、浮动范围、金额在回调/查单中的含义；未确认前只接受金额不变。异常金额不展示二维码、不自动开通，保留人工核查。
5. 确认二维码有效期、通知重试策略、挂机离线后的补报能力、延迟付款的时间字段。
6. Demo 内凭据不用于生产，也不复制进仓库；如其中是实际商户凭据，应在平台更换并只存 Secret。

## 4. 推荐 API（复用官网现有命名）

- GET /v1/billing/plans：返回已启用套餐和服务端定价。
- POST /v1/billing/checkout：已登录，body 仅 planId/channel；Idempotency-Key 必填，防重复点击/网络重试重复下单。
- GET /v1/billing/orders：已登录，仅本人，游标分页。
- GET /v1/billing/orders/:orderNo：已登录，仅本人，查本地状态，不在每次轮询时请求平台。
- GET /v1/billing/notify/ezfpy：平台回调，置于用户 auth 拦截前，独立验签与校验。
- POST /v1/admin/billing/orders/:orderNo/reconcile：受独立服务端管理员认证保护、限频、审计；第一版由私有运维脚本调用，不把 ADMIN_API_KEY 放进网页。

这是对提示词 /api/payment/* 的有意调整，减少两套路径和前端返工。响应沿用现有 API 风格；若采用 success/data 包装，支付适配层显式解包，不破坏其他 API。

## 5. 模块分层

payments/types.ts：PaymentProvider、VerifiedPayment、CreatePaymentResult、PaymentQueryResult。
providers/ezfpy.ts：请求平台、协议解析、验签、查单；不得修改会员。
providers/mock.ts：模拟平台，生产环境绝不注册/开放模拟成功入口。
providers/index.ts：注册与选择，由服务端配置决定，禁止前端选择 mock。
ezfpy-sign.ts、money.ts：统一签名/验签和金额转换。
orders.ts：套餐快照、下单、状态、订单归属、幂等键。
payment-settlement.ts：唯一的入账/权益发放入口；notify 和 reconcile 共用。
reconcile.ts：主动查单与异常恢复；平台未识别付款就保持待确认，无法绕过挂机软件证明到账。
routes.ts：请求限制、认证接入、输出净化。

金额用整数分；严格十进制解析，拒绝负数、科学计数法、超过两位小数、溢出。PID 按字符串处理。签名用原始值，传输才由 URLSearchParams 编码；不随意 stripslashes，不将密钥前缀写成 &key=。
MD5 仅用于协议兼容，选可在 Worker 运行的小型实现，以已知测试向量及 PHP 对照验证；不修改密码/会话哈希，不假定 subtle.digest 支持 MD5。

## 6. D1 增量设计

拟新增 0003_payments.sql；同步 schema.sql 仅用于新环境。

plans：id、name、price_cents、duration_days、membership_level、enabled、时间字段。产品价格/时长待确认，生产套餐默认禁用，提示词中的 19.90 不是已确认售价。
orders：id/order_no/user_id/plan_id、下单时名称/时长/等级快照、amount_cents/payable_cents、币种、provider/channel/provider_trade_no、status、create_state、expires_at/paid_at/settled_at、last_error_code、幂等键和请求指纹、时间字段。
- order_no 唯一；(user_id,idempotency_key) 唯一；(provider,provider_trade_no) 对非空值唯一，阻止一笔平台支付绑定多个本地订单。
- 索引覆盖本人订单分页、状态+过期、补偿候选，禁止全表加载再用 JS 过滤。
payment_events：订单关联、事件种类、脱敏原因码、金额、平台单号、验证结果、request_id、时间；限制日志长度和保留周期。
payment_settlements：order_id 唯一，权益快照与入账时间；作为一次性发放凭据。

subscriptions 不复制；会员到账不清零 usage_monthly.chars_used/task_count。明确 Pro 购买是提升月额度上限，不是每次购买额外赠送一个月额度包。

## 7. 原子结算和竞态

不能仅“UPDATE pending→paid 成功后再单独 UPDATE subscription”：后一步失败会留下已付款但无会员。
推荐以唯一 payment_settlements 插入作为原子入口，配套 D1 SQLite 触发器在同一数据库语句事务中完成：订单条件校验→订单 paid→subscriptions UPSERT→必要的额度上限更新→审计。任一步失败整体回滚。
- 后续实现必须在本地 D1 兼容运行时验证触发器/回滚，不能只用 JS 模拟 changes=1。
- 重复合法通知：复核金额、PID、渠道、平台单号等一致，已结算则返回 success，不重复加时。
- 同一用户两笔订单并发：到期计算在数据库中进行，避免两个请求读取旧到期日后互相覆盖。
- 续费：max(当前有效同级 Pro 到期时间, 当前时间)+下单时长；过期从当前时间起。暂不售卖不同等级切换套餐。
- 执行已成功但 ACK 丢失：重试仍只发放一次。
- 先到回调后到下单响应：create_state 区分 creating/ready/unknown/rejected，未知实际应付金额时不抢先入账；留待平台重试/查单。下单响应只能补字段，不能覆盖 paid。
- 网络超时不等于平台创建失败：保留 unknown，复用同一订单查证，禁止自动换订单号重复扣款。
- 本地 expired 不是平台未收款证明；晚到的有效付款进入核查/查单恢复。退款/取消等不可直接复活。缺少可靠支付时间或协议依据时保留待核查，不默默丢弃到账。

## 8. 通知安全

有长度上限的 GET 参数解析→必填项/签名类型→签名→PID→TRADE_SUCCESS→订单存在/provider/channel→实际应付金额→平台单号绑定→原子结算。
回调金额不得反过来修改 payable_cents；用户身份、套餐和权益只从本地订单取，param 仅附加信息。
签名采用固定长度比较，避免常规提前退出；不记录签名原文/含 key 的查询 URL/完整请求。
只有提交完成或完全一致的重复成功通知返回 200 text/plain success；数据库失败或未完成结算不提前 ACK。
WAF/Access 仅为回调精确路径处理例外，不关闭整个 API 的防护；回调不使用浏览器登录、验证码或 Turnstile。

## 9. 主动查单和运维

优先 Demo 带 PID/key 的接口，不使用未知权限模型的 /api/findorder。
后端固定 HTTPS Base，禁止用户指定上游 URL；含 key 的请求禁止跟随到其他域名的重定向，不输出完整 URL 到异常、日志或前端。
查单响应必须完整确认订单归属、单号、已付状态、金额及可用身份字段；字段不足或语义未确认则禁止自动结算。
管理员触发查单，走同一 settlement。没有真实成功凭据不提供“设为 paid”。
首版不自动创建后台定时任务；后续如需要补偿调度，再确认触发频率、配额和最大查询量。

## 10. 前端与 APP

复用官网 billing.html，新增独立 billing.js 和支付样式；套餐由服务端读取。
登录→选择套餐/渠道→提交幂等下单→显示实际应付金额/二维码/单号/倒计时→每 3 秒查询本地订单，退避并在隐藏/离开页面停止。
qrcode 使用本地打包二维码库渲染，不发送给外部二维码生成服务；code_url 只允许已确认的 HTTPS 图片来源，相对路径先按平台 Base 解析并验证；禁止把第三方 HTML 插入页面。
移动端仅对确认安全的支付 URL/协议提供打开按钮，不能把任意 qrcode 当作可跳转 URL。
付款完成刷新 subscription/usage；回跳页面只读本地状态。超时显示“待确认/可重新查询”，不直接断言用户未付款。
APP 第一版打开官网会员页面，浏览器自行登录，不将 APP Bearer 放进 URL；回 APP 手动刷新，必要时增加窗口激活后的节流刷新。

## 11. CF 配置计划

普通变量：EZFPY_API_BASE_URL、EZFPY_PID、PUBLIC_WEB_URL、PUBLIC_API_URL、PAYMENTS_ENABLED（初始 false）。
Secret：EZFPY_KEY；启用运维补单时配置 ADMIN_API_KEY，不能复用商户 Key。
PUBLIC_WEB_URL 使用现有官网；PUBLIC_API_URL 使用现有 Worker 公网地址，是否切自定义域名另行验证，不使用提示词中的示例域名。
既有 DB、认证与邮件 Secrets 全部保留。Pages 和 APP 不配置支付密钥。
部署次序：测试→备份/核对生产状态→增量迁移→支付关闭状态部署 Worker→前端部署→配置平台/小额验收→启用正式套餐。
回滚通过关闭新下单；保留 notify/reconcile 接收已创建订单，不删除订单/结算表，不回滚已生效权益。

## 12. 测试与验收门槛

A. 签名/金额：固定向量、中文、0.01/19.90、空值与零、排除字段、顺序、错误 key、编码、重复参数、反斜杠策略。
B. 下单：未登录/停用套餐/错误渠道/伪造价格/重复幂等键/不同请求复用键/限流；HTTP 失败、业务失败、非 JSON、缺少二维码或单号、金额异常、超时未知。
C. 回调：签名/PID/状态/金额/订单/provider/channel/平台单号不符；合法重复/并发、回调早到、ACK 丢失。
D. 数据库：首购、过期重购、提前续费、同用户多单并发、事务中故障整体回滚、已用额度保留、会员过期后额度回落。
E. 补单：漏通知、延迟通知、平台仍未识别、响应身份/金额不符、过期订单、越权、限频。
F. 安全：production 禁止 mock；浏览器/日志/安装包无商户 key；订单不可越权；第三方 URL/HTML 净化。
G. 回归：npm test、npm run typecheck、Wrangler dry run；官网桌面/移动支付状态；原有登录、邮件、翻译预留与术语库不能退化。
H. 经用户授权后，两种渠道各做一笔平台允许的最低金额真实付款；确认挂机→通知→仅一次入账→官网及 APP 会员/额度同步，并测试重复通知不重复续费。

本轮所有实现测试/真实支付验收均为 NOT TESTED；仅完成文件和协议材料审阅。

## 13. 预计修改文件

后端根目录 D:/DWGC2E/cf-worker/：
- 修改 src/index.ts、schema.sql、wrangler.toml、package.json、README.md；依赖变更才更新 package-lock.json。
- 新增 src/payments/ 下上述模块、migrations/0003_payments.sql、tests/payment-*.test.mjs。
- 按需要提取 Env/认证共享类型，限于支付接入必需，避免大规模重构。
官网根目录 D:/DWGC2E_Website/：
- 修改 billing.html、js/api-client.js；按实际 DOM 修改现有相关页面脚本。
- 新增 js/billing.js、css/billing.css、本地二维码资源（遵守许可证）。
- 按需要修改账户页面会员入口和登录后的站内返回逻辑。
APP D:/DWGC2E/src/DwgTranslator.App/：
- 仅在已有入口/刷新不足时改会员导航与账户刷新，不预先承诺必须改 APP。
文档/运维：新增支付部署验收说明和私有补单脚本；不保存实际凭据。
预计不删除现有业务文件。

## 14. 分阶段交付

阶段一：确认套餐政策与平台协议缺口，冻结字段/状态/金额规则。
阶段二：增量数据库+订单/结算+Mock，本地完成并发与故障注入验收。
阶段三：EzfpyProvider+签名/通知/查单+安全测试。
阶段四：官网支付 UI、APP 会员刷新与原有接口回归。
阶段五：用户授权部署和小额真付；缺真实响应或商户配置不能宣称上线完成。

仍需用户确认：正式套餐价格和时长、Pro 是否沿用每月 100 万字符、是否接受平台浮动金额及允许范围。平台凭据在 CF 私密填写，无需发送到聊天。

## 15. 用户确认：测试价格（2026-09-14）

用户指定先使用人民币 0.19 元和 0.29 元进行测试。
- 两档测试金额分别保存为整数 19 分和 29 分，不是正式上线售价。
- 测试套餐仅供受控验收使用，正式公开销售前替换/确认商品配置。
- 用户尚未指定两档各自对应的会员时长和权益；不得自行将其解释为月付/年付，也不覆盖正式套餐。
- 本次只更新计划，没有创建真实支付订单、修改生产数据库或启用收款。

## 16. 用户确认：测试时长（2026-09-14）

用户确认测试时长为 7 天，替代先前建议的 1 天：
- 测试套餐 A：人民币 0.19 元（19 分），Pro 7 天。
- 测试套餐 B：人民币 0.29 元（29 分），Pro 7 天。
- 两档均用于支付链路测试，不代表正式月付/年付定价。
- 按本计划同级续费规则：有效 Pro 在当前到期日后增加 7 天；无有效 Pro 从结算时起增加 7 天。重复通知不得重复增加时长。
- 沿用当前 Pro 每月 100 万字符的额度上限，不因每次购买重置已用量或额外叠加额度包。
- 本轮仅更新已确认的计划参数，未修改线上套餐、部署或发起真实付款。

## 17. 实施状态（2026-09-14，替代前述“仅计划”状态）

APP、网站、Worker 已完成本地实现，部署说明见 `EZFPY_CF_SETUP.md`。本地 Worker 84 项测试、Core 82 项测试、类型检查、APP 编译与发布烟测、Wrangler dry-run、本地 D1 重复结算、网页模拟支付桌面/手机烟测通过。未部署生产、未真实支付；原有业务的线上联调仍需验收。

相对原计划的保守调整：未验证真实查单响应前只提供管理员只读 inspect，不实现自动补单；平台请求通过测试 fetch 模拟，不注册可在线使用的 MockProvider；不接受浮动金额；创建结果未知不重建上游订单；提前到达的通知需平台重试；网页到期不代表平台撤单。正式价格、退款、自动补单、手机拉起仍不在当前测试版交付范围。

## 18. 生产兼容修复与部署（2026-09-14）

经用户授权已直接备份/升级 D1 并部署 Worker。旧 orders 表为 8 字段，原迁移未兼容已有同名表，已新增一次性 legacy upgrade 并通过本地 D1 与 4 项新增回归。全套 88 项测试通过。生产订单表已升级，Worker 版本 d1e98b94-155c-4223-a151-98d48c7c7ab2；既有配置保留，支付仍关闭，待商户信息和白名单。具体记录见 EZFPY_CF_SETUP.md 第 8 节。

## 19. 用户要求全量开放（2026-09-14）

前述测试邮箱白名单政策已取消。所有通过真实会话认证的用户都可按 0.19/0.29 元购买 Pro 7 天；后续再调价。Worker 已移除白名单逻辑并部署，官网文案更新已发布，APP 文案已改为不写死价格且完成新包构建。部署与包路径详见 EZFPY_CF_SETUP.md 第 10 节。按用户要求没有运行额外测试或真实支付。
