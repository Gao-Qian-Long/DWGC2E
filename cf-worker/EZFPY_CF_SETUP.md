# 易支付测试版：CF 配置与上线验收

日期：2026-09-14。最新状态：网页、Worker 和生产 D1 兼容升级均已完成；支付关闭，尚未真实付款。下面原始操作步骤仅作维护参考，当前生产库不要重复执行迁移。详见文末第 8 节。两档均为 Pro 7 天（0.19 / 0.29 元），续费延长时长，月额度上限 100 万字符，不清空已用量。

## 1. 上线顺序

1. 保存当前 Worker 的普通变量、绑定配置，并按现有流程备份 D1。不要将 Secret 值发到聊天或提交仓库。
2. 确认生产库已包含既有 users、subscriptions、usage_monthly、request_limits 等业务表，且已有 0002 安全计费迁移。若不确定，先检查结构；不要盲目重跑全量 schema.sql。
3. 对现有生产库只执行新增支付迁移 `cf-worker/migrations/0003_payments.sql`。不要执行 artifacts 中的本地烟测 SQL。
4. 核对 wrangler.toml 的既有线上普通变量，保持支付关闭部署新 Worker。
5. 配置商户参数和测试邮箱；发布新网站；最后启用测试支付。
6. 用下面的新 APP 和同一邮箱账号进行真实验收。

用户自行执行的命令（本轮未执行远程命令）：

```powershell
cd D:\DWGC2E\cf-worker
npm run typecheck
npm test
# 确认目标库、备份与既有结构后才执行：
npx wrangler d1 execute dwg-translator-prod --remote --file=./migrations/0003_payments.sql
# 先将本地普通变量与线上现有值同步，并保持 PAYMENTS_ENABLED=false：
npx wrangler deploy
```

**重要：本地 DOWNLOAD_URL 等仍为空。不能未经核对直接部署并覆盖线上已有值。** Dashboard 设置与 Wrangler 配置应保持一致；不要认为 Dashboard 填好后下次部署就一定保留。本项目应同步维护 Wrangler 配置，敏感值使用 Secrets。

## 2. Worker 配置

Cloudflare → Workers & Pages → dwgc2e-api → Settings → Variables and Secrets。普通值使用文本变量；密钥必须使用 Secret。保存后按控制台提示部署生效。

| 名称 | 类型 | 值 / 说明 |
|---|---|---|
| PAYMENTS_ENABLED | 文本 | 部署时 false；其余就绪后 true |
| PAYMENTS_TEST_USERS | 文本 | 你的已注册账号邮箱；多个用英文逗号分隔。不填则无人能下单 |
| EZFPY_PID | 文本 | 平台真实商户 PID |
| EZFPY_KEY | Secret | 平台真实商户密钥，不放 APP、网页或 wrangler.toml |
| EZFPY_API_BASE_URL | 文本 | https://www.ezfpy.cn/；如商户平台另行指定，以确认后的地址为准 |
| PUBLIC_WEB_URL | 文本 | https://cad.pocketter.dpdns.org |
| PUBLIC_API_URL | 文本 | https://dwgc2e-api.maplehousezz.workers.dev |
| EZFPY_QR_IMAGE_ORIGINS | 文本 | 先空。仅当平台返回其他可信 HTTPS 域的二维码图片时，填核实后的 origin，多个英文逗号分隔；不使用通配符 |
| ADMIN_API_KEY | Secret，可选 | 独立随机密钥，至少 32 字符，仅用于管理员只读查单；不用查单可不配置 |

Bindings 保留现有 `DB` → `dwg-translator-prod`。保留邮件、认证、翻译服务已有配置与 Secrets，不需要重新建库或把商户密钥放 Pages。

通知地址（后端下单自动传给平台；若平台要求后台配置则填同一个）：

```text
https://dwgc2e-api.maplehousezz.workers.dev/v1/billing/notify/ezfpy
```

如域名设置了 Access/WAF/人机验证，不得拦截平台对此精确路径的通知；不要关闭全站安全规则。浏览器 return 页面不是到账依据。

## 3. 网站和 APP 发布

网站改动在 `D:\DWGC2E_Website`，不是 APP 仓库。按原有 Pages 发布流程发布此目录的新文件，包括 billing.html、js/api-client.js、js/billing.js、css/billing.css、js/vendor 下二维码资源。尚未替你提交、推送或发布。

新 APP：

```text
D:\DWGC2E\artifacts\publish-20260914-222716\DwgTranslator.exe
D:\DWGC2E\artifacts\DwgTranslator-win-x64-GstarCAD-20260914-222716.zip
```

请运行新目录中的程序，或完整解压 ZIP 后运行；不要仅复制 exe。原 release 目录没有替换。APP 会员入口打开官网，不传登录令牌；官网须用相同账号登录，付款后返回 APP 点击刷新。

DOWNLOAD_URL 与支付无关：需要开放下载/更新时，先上传真实完整安装包，再填可公开直接下载的 HTTPS 地址；暂不做更新可留空。不要填本机路径或需要登录的分享页面。

## 4. 小额真实验收（由你操作）

- 登录已加入白名单的账号，确认 0.19 / 0.29 元两档均显示 7 天。
- 分别验证支付宝和微信渠道；只支付页面和钱包均确认正确的金额，不接受浮动金额。
- 成功通知后，官网订单变为已支付、二维码消失、会员到期时间增加 7 天。
- 返回 APP 刷新：会员、到期时间、额度一致；同账号术语库上传/下载及实际翻译正常，翻译成功正确扣减额度。
- 第二次购买应续加 7 天，不重置已用字符。重复通知不得重复续期。
- 未列入测试邮箱的账号不可新下单；非订单所有者不可读取订单。

## 5. 当前边界与故障处理

- 本地测试使用模拟平台响应，不能证明真实商户接口/渠道已经可用。
- 平台真实查单响应尚未核实，所以管理员 inspect 只读查单、不会自动补单。漏通知、创建超时未知或通知早于下单结果的情况，不保证自动恢复。
- 创建结果未知时页面保留幂等键，不自动再创建新平台订单。若已扣款但未到账，先记录订单号、保留平台凭证，不要反复购买；核实平台记录与通知后再处理。
- 二维码页面有效期约 15 分钟；已正确绑定的待付订单仍可接收并校验延迟通知。页面倒计时结束不等于平台撤单。
- 本版没有退款入口、自动补单、手机支付拉起或正式定价。手机页面目前提示使用另一设备扫码。
- 停止测试：将 PAYMENTS_ENABLED=false，停止新下单；保留 Worker 通知路由和商户配置处理在途订单，不删表、不立即轮换密钥。

## 6. 本地验证记录

Worker 84 项测试、TypeScript 检查、Wrangler dry-run；本地 D1 建表及重复结算烟测；APP 构建 0 警告 0 错误、Core 82 项测试、发布包/UI 烟测；网页桌面与 390px 手机视口模拟支付烟测均通过。日志与截图在 artifacts/payment-*。真实支付与生产联调仍待验收。

官方配置参考（本轮网页工具未返回正文，操作前请以控制台和官方文档为准）：

```text
https://developers.cloudflare.com/workers/wrangler/configuration/
https://developers.cloudflare.com/workers/configuration/secrets/
https://developers.cloudflare.com/d1/get-started/
```

## 7. 后续发布结果（2026-09-14）

按用户后续授权，网站已提交并推送至 GitHub main：`5d9e46b9276460425f10356b044d01d37875faaa`。Cloudflare Pages 检查返回 success；自定义域名和 pages.dev 的 billing.html（带版本查询参数）均返回 HTTP 200 且包含新支付脚本。此前第 3 节“未推送/发布”描述已由本节更新。Worker/D1/商户配置仍未远程修改，真实支付未验收。

重新构建且已安装到本地 `D:\DWGC2E\release\DwgTranslator.exe`，保留原便携配置数据。分发包使用干净默认配置：

```text
D:\DWGC2E\artifacts\DwgTranslator-win-x64-GstarCAD-20260914-230214.zip
```

旧版备份：`D:\DWGC2E\artifacts\release-backup-20260914-230214`。
版本：`2.1.0+ui.20260914-230214.23ade57`。
EXE SHA256：`B4B81A6AEE59C3F28D69DCD93BD411A3DC0A3A3CC7BBAAA1D7A0BB4875D9D8F5`。
本次构建重新通过 Core 82 项测试、APP UI 烟测、CAD 插件与发布包检查；日志 `artifacts/payment-final-publish.log`。本版本是支付测试版，不代表真实支付已验收。

## 8. 生产 D1 兼容修复及 Worker 已部署（2026-09-14 23:12）

用户明确要求代理直接处理可执行的 CF 操作。已只读检查发现生产旧 orders 表只有 8 个字段，不是此前干净数据库测试中的完整支付表。CREATE TABLE IF NOT EXISTS 不会补字段，因此原 0003 在创建 provider 索引时失败。新增 `cf-worker/upgrades/0003_payments_from_legacy.sql` 专用于该旧结构；先补字段、隔离历史订单，再执行标准支付迁移。不删表、不重建库、不把历史订单视为可结算新订单。

已执行并确认：
- 先导出完整生产备份到本地被 Git 忽略的 `artifacts/payment-d1-backup-20260914-2309.sql`。该文件包含账号相关敏感数据，不应上传或公开分享。
- 对实际旧表结构完成本地 D1 导入验证；新增 4 项兼容测试，全套 Worker 88/88 通过，类型检查通过。
- 远程兼容升级成功：orders 现有 21 字段、两档套餐和两项支付结算触发器就绪；升级前后 users=2、subscriptions=2、orders=0、usage_monthly=0。
- 已部署新版 Worker，版本 ID `d1e98b94-155c-4223-a151-98d48c7c7ab2`。部署前后核对既有普通变量值、Secrets 名称与 DB 绑定一致。
- 线上只读检查：根路径和版本接口 200；未登录支付接口 401；通知接口因尚无商户配置返回预期 503。未发邮件、未创建支付订单、未测试真实付款。

当前不要再次执行旧表兼容升级（一次性 ALTER），也不需要再次部署当前 Worker。PAYMENTS_ENABLED=false。剩余需要商户 PID、Secret EZFPY_KEY 及用户确认的测试邮箱，然后才能启用支付。网站和 APP 本次无需重新发布，因为仅新增数据库升级文件/测试/文档。

## 9. 支付已开启（2026-09-14）

用户明确要求直接开启且不做额外测试。已部署 PAYMENTS_ENABLED=true，并读取线上版本确认，版本 c7fd6c4e-cd1c-4875-b25c-f16d5e4828b5。原商户配置、测试邮箱和其他既有变量保留；白名单未移除，价格仍为 0.19/0.29 元 Pro 7 天。没有创建订单或真实付款。网页/APP 静态审查见 PAYMENT_GO_LIVE_REVIEW.md，发现的问题尚未修复。

## 10. 全量已登录用户开放购买（2026-09-14，覆盖前述白名单规则）

用户明确要求：所有已登录用户按当前 0.19/0.29 元购买，后续再调价。已移除 Worker 邮箱白名单判断；PAYMENTS_TEST_USERS 即使仍存在于 Dashboard，也已不参与权限判断。仍保留真实登录、订单归属、服务端定价、验签、下单限流和全局支付开关。

- Worker 已部署：4b2794e1-2068-4949-9249-8b93a721ef5b。PAYMENTS_ENABLED=true；原有普通变量、Secret 绑定名称、DB 绑定核对保留。
- D1 已执行 0004_public_membership_names.sql：套餐显示名改为 Pro 7 天 A/B，价格 19/29 分、时长 7 天、Pro 权益和启用状态不变。保留稳定商品 ID test_pro_019/test_pro_029，避免影响历史订单；ID 含 test 不代表访问受限。
- 官网正式购买文案已推送 main，提交 34b294b，Cloudflare Pages success。
- APP 会员入口改为中性开通/续费文案，不再写死价格，后续调价以 D1 plans 为准。APP 已构建到 artifacts/publish-20260914-232535/DwgTranslator.exe，完整 ZIP 为 artifacts/DwgTranslator-win-x64-GstarCAD-20260914-232535.zip。
- 用户正在运行 release 旧版，本轮未强制结束进程、未覆盖运行中程序；新包可在关闭旧版后使用。
- 按用户要求跳过测试套件/支付测试；只进行类型检查、发布编译与必要发布包检查、部署状态核对。无真实订单/付款。发布脚本新增显式 -SkipTests 参数，默认仍执行原有测试。

后续调价修改 plans.price_cents（整数分）和需要的商品名称/权益；网站读取服务端商品，不需要重新发 APP。已创建订单保留自己的价格快照，不应把历史订单金额随套餐一起修改。
