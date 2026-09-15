# 支付开启后的网页 / APP 静态审查

日期：2026-09-14。按用户要求，本轮不运行额外测试、不创建订单、不真实付款；检查代码和部署配置，不等同真实支付验收。

## 部署结果

PAYMENTS_ENABLED=true 已部署并读取线上版本确认。版本 c7fd6c4e-cd1c-4875-b25c-f16d5e4828b5。原有普通变量值（除开关）及 Secret 绑定名称保留。PAYMENTS_TEST_USERS 白名单未移除，当前仍是受控开放，不是所有注册账号都能购买。价格仍为 0.19/0.29 元、Pro 7 天。

本地 wrangler.toml 同步开关，启用 keep_vars，移除商户 PID/测试邮箱的空值占位，避免下次部署以空值覆盖控制台配置。密钥不进入配置文件。

## Findings（静态审查发现，尚未修复）

### P1：公开下载入口尚未对齐本次新 APP
- D:/DWGC2E_Website/js/site-config.js:3-4 仍为 version=1.0.0 和既有蓝奏云地址，而本次构建为 2.1.0。
- Worker DOWNLOAD_URL 仍为空。本地新 ZIP 未上传到公开下载地址。
- 影响：无法保证新用户从官网拿到本次支付版 APP；官网显示版本已确定不一致。没有检查旧分享链接内文件，不断言它一定包含哪个版本。
- 建议：上传新完整 ZIP、更新官网版本/下载地址及 Worker 下载配置。

### P2：过期订单使同套餐同渠道无法直接重新下单
- D:/DWGC2E_Website/js/billing.js:22-23。
- pending 只在切换套餐/渠道或 show(paid) 时清除；超过二维码窗口后，点击同套餐仍复用旧幂等键，服务端返回原过期窗口订单，不产生可用新二维码。
- 建议：增加明确的重新下单流程；先展示旧单状态并提示已付款勿重下，不将 unknown 创建状态自动视为未付款。

### P2：查看任意历史已支付订单会清掉当前待付记录
- D:/DWGC2E_Website/js/billing.js:18。
- show(o) 对任意 paid 订单都 pending=null；pending 未保存 orderNo，因此无法判断显示的已付订单是否对应当前下单请求。
- 影响：已有待付新单时查看另一笔历史 paid 单，再点击套餐会生成新幂等键/新订单，丢失原请求的本地防重复保护。服务端每个订单的结算幂等仍然存在，这不是单笔回调重复入账。
- 建议：保存并比较 orderNo，仅清理与成功订单匹配的待付记录。

### P2：APP 套餐文案写死，仍为测试态
- D:/DWGC2E/src/DwgTranslator.App/Views/Pages/AccountPage.xaml:37-38。
- APP 固定显示“支付测试”和两档价格；网页也保留测试文案。这与全量正式销售表述不一致。
- 影响：未来服务端调整套餐后，APP 文案不能自动同步，需重新发包；浏览器下单金额仍来自服务器，不受此显示文案控制。
- 建议：APP 改为中性会员入口、具体权益/价格由服务端套餐或官网展示。

## 既有功能边界（非本轮新发现）

- APP 付款后需手动刷新，没有支付回跳/自动同步。
- 尚无自动补单、退款入口、手机支付直接拉起；创建超时未知或通知未达需要人工核实。
- 商户配置存在不代表平台一定接受该 PID/密钥组合；按用户要求本轮未验证真实下单。

本轮只完成开启支付、保护后续部署配置及审查；未修改/重新发布网页和 APP，也未声称生产支付无 BUG。

## 后续状态（2026-09-14）

用户已明确要求向所有已登录用户开放，邮箱白名单限制已移除并部署。官网/APP 的测试文案已移除；APP 改为中性会员入口、不再写死价格，因此上述“APP 套餐文案写死”项已在新构建包中解决。新包未覆盖正在运行的旧 release。下载入口未对齐及两个 pending 订单状态问题未在本轮修复。

## 2026-09-14 QR diagnosis follow-up
- Read-only production D1 inspection: three orders are pending/create_state unknown, no provider trade number; old error capture discarded original causes. No confirmed root cause for these historical orders.
- Fixed valid QR payload being discarded solely because optional image URL fails origin validation. Untrusted images remain blocked. Financial checks unchanged.
- Added fixed, non-sensitive diagnostic categories to future order failures and owner-visible status. No raw provider messages/credentials logged.
- Updated private read-only provider query to documented POST api/findorder (order_no/type); no automatic settlement or retry.
- Website now shows no-QR state rather than scan instructions/countdown for unknown creation. Existing orders not recreated or modified.
- TypeScript and JS syntax checks passed; no test suite or real checkout run per user instruction.
- Worker deployed: 70e1d347-e0a6-4dad-947f-fa8ed5feac6c. All remote binding metadata/ordinary values preserved. Site commit 717b4d5, Cloudflare Pages check succeeded.
- Direct provider query during this turn timed out from local network; this does not establish Worker connectivity failure. Exact historic upstream response cannot be recovered.

## 2026-09-14 unpaid order list removal
- Added owner-authenticated POST orders/:orderNo/hide. Only hides unpaid orders; no physical deletion, provider cancellation, refund, or settlement changes. Paid records reappear even if payment races with hiding.
- Additive migration 0005_payment_order_hidden applied remotely. No existing records hidden by agent.
- Frontend confirmation explains old order remains payable upstream; resets matching pending checkout after explicit removal. Cross-tab stale idempotency gets order_hidden rather than silent recreation.
- Latest D1 read before change still showed the same three historical unknown orders, no new response available to identify QR failure.
- TypeScript and JS syntax checks passed; no test suite or real checkout performed.
- Worker b336b062-ea09-44cb-ae04-c79cfd4501e4 deployed; website commit 14224e9 Pages succeeded.

## Same-origin browser API connectivity fix
- Local requests to workers.dev root and preflight timed out; website custom domain returned 200. Screenshots showed network failure / 12-second client abort before membership loaded.
- Added fixed-upstream Pages Function /api/*; browser API base now /api. Only approved request headers forwarded, redirects blocked, no cache, no raw errors logged. Admin and payment notification routes not proxied. Existing Worker/DB/secrets unchanged.
- Updated HTML config/API cache versions and readable network/timeout messages; loading placeholders no longer remain indefinitely after initialization failure.
- JS syntax checks passed. No real order/payment or test suite executed.
- Production deployed directly using Wrangler Pages: 64075739.dwgc2e-website.pages.dev. Version route 200; subscription and order routes return expected unauthenticated 401; current custom-domain config uses /api.
- Website local commit b2f6b3f. GitHub push failed repeatedly due connection reset/timeouts; direct CF deployment is live, repository sync still pending. Do not redeploy old GitHub main over this fix.

## QR presentation re-review
- Latest production order DW2246829de90c4c739cb82f69a3b5d1ec (2026-09-14T16:04:20.099Z) has null qr_code, qr_image_url, provider_trade_no; unknown / create_unconfirmed. This is not evidence of a browser-only rendering failure. Generic diagnostic still does not identify upstream cause.
- Targeted execution of actual vendored QR library + renderer with fake DOM/canvas: valid payload produces 222px canvas; image-only path works. This is not a visual browser or real payment verification.
- Found/fixed canvas failure bypassing image fallback, and blank ready/empty or expired states; scan instructions hidden when no ready QR is available.
- Post-fix targeted checks confirmed canvas, image fallback, and empty-state text. No real orders created; no broad test suite.
- Website pushed commit 2619623.
