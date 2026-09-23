# QLCAD Cloudflare Worker

## 一次性部署顺序

```powershell
cd D:\DWGC2E\cf-worker
npm install
npx wrangler login
npx wrangler d1 execute dwg-translator-prod --remote --file=./schema.sql
npx wrangler deploy
```

`DB` 是 Worker 绑定的 D1 数据库，`database_id` 已写在 `wrangler.toml`。执行 schema 前请确认是在生产库上执行；重复执行带有 `IF NOT EXISTS` 的建表/索引语句是安全的。

## 必须配置的 Secrets（不能写入代码、Pages 或 Git）

```powershell
npx wrangler secret put PASSWORD_PEPPER
npx wrangler secret put JWT_SECRET
npx wrangler secret put DEEPSEEK_API_KEY
npx wrangler secret put BREVO_API_KEY
```

- `PASSWORD_PEPPER`：固定的随机长字符串；以后不要更换，否则历史密码哈希无法验证。
- JWT_SECRET：用于会话签名，只放 Worker Secret。
- `DEEPSEEK_API_KEY`：只放 Worker，用于服务端翻译，不下发到 APP。
- `BREVO_API_KEY`：只放 Worker，用于注册/找回密码验证码邮件。

## wrangler.toml 中的公开变量

当前仓库已经提供默认值，重点确认：

- 当前 Worker 地址：`https://dwgc2e-api.maplehousezz.workers.dev`（绑定自定义域名后再替换）

- `CORS_ORIGINS = "https://cad.pocketter.dpdns.org"`
- `DEFAULT_PLAN = "free"`
- `MAIL_FROM = "QLCAD <noreply@mail.cad.pocketter.dpdns.org>"`
- `LATEST_VERSION = "2.1.0"`
- `DOWNLOAD_URL`：填写可公开访问的最新 EXE 直链；没有直链时留空
- `BACKUP_DOWNLOAD_URL`、`RELEASE_NOTES`：可留空

不要把任何 Secret 填进 `[vars]`，也不要把 API Key 配进 APP。

## 部署后检查

```powershell
cd D:\DWGC2E
.\tools\Test-WorkerContract.ps1 -BaseUrl https://dwgc2e-api.maplehousezz.workers.dev
```

重点验证：注册/登录、验证码邮件、找回密码、额度、设备、`/v1/glossary`。未登录访问 `/v1/glossary` 应返回 `401`；登录后 GET/PUT 术语库最多 1000 条。



## 双邮件平台轮流发送（Brevo + Resend）

两个平台都保留，注册和找回密码共用全局轮换队列：Brevo → Resend → Brevo → Resend。两个平台均须允许 MAIL_FROM 对应的发件人，Worker 中须保留 BREVO_API_KEY 和 RESEND_API_KEY 两个 Secret；无需重配已验证的 DNS。

普通变量（已写入 wrangler.toml）：

- MAIL_PROVIDER=round_robin：两个平台轮流作为首选；brevo 或 resend 可固定首选平台。
- MAIL_FALLBACK_ENABLED=true：首选返回 401/402/403/429/5xx 时尝试另一平台一次；false 关闭备用，但不关闭轮流选首选。
- 只有一个平台有密钥时，轮换模式只使用该平台。两个密钥都缺失或配置非法时不发送。

轮换使用 D1 的单条原子 UPSERT RETURNING，不依赖 Worker 内存。新增 mail_routing_state 表仅保存一行 0/1 状态；双平台模式每次分配增加一次 D1 写操作，受现有 D1 配额限制。备用重试不再推进轮换。限流/配置检查在分配之前执行。分配后若验证码写库或投递失败，该轮仍已消耗，因此均衡的是首选分配次数，不保证送达数量精确 50/50，也不根据平台剩余额度加权。

网络异常/超时的投递结果不确定，不自动跨平台重发；5xx 切换时仍可能收到同一验证码的两封邮件。API 成功只表示平台接受请求，不表示最终到达收件箱。全站用户共享轮换，其他用户请求或并发会影响你看到的顺序。

### 已有生产库升级（先加表，再部署）

1. CF → dwgc2e-api → Settings → Variables and Secrets：确认两个 API Key 均为加密 Secret，不要删除 Brevo，不要把密钥写入 Git。
2. 核对本地 wrangler.toml 的 MAIL_FROM、下载地址等普通变量，避免覆盖 Dashboard 中的不同值。
3. 执行以下命令；这里用独立增量文件，不重建或清空已有用户数据库：

```powershell
cd D:\DWGC2E\cf-worker
npm test
npm run typecheck
npx wrangler d1 execute dwg-translator-prod --remote --file=./migrations/0001_mail_round_robin.sql
# 上一步成功后再执行部署
npx wrangler deploy
```

增量建表可重复执行，不重置现有轮换位置。如果忘记建表，发送接口返回 503 / mail_routing_unavailable，不发送邮件且不作废上一次验证码。不要通过重建数据库来修复。

### 用户验收

```powershell
cd D:\DWGC2E\cf-worker
npx wrangler tail --format pretty
```

在官网申请验证码、等倒计时结束后再申请一次，并检查两个平台的发送记录和收件箱。不要频繁点击触发限流；新验证码会使旧验证码失效，以最新一封为准。
日志 verification_mail 包含 primary、attempt、provider、HTTP status、request_id，不含邮箱、验证码或密钥。正常无其他请求时，两次 attempt=1 的 provider 应不同；attempt=2 表示备用尝试。

需要分别排查平台时，临时将 MAIL_PROVIDER 设为 brevo 或 resend，MAIL_FALLBACK_ENABLED=false 并部署；验收后恢复 round_robin / true。回退到固定平台不需要删表。

本地测试模拟邮件 HTTP 请求，并用 SQLite 验证轮换 SQL、持久化、独立连接分配；不发送真实邮件，不代表生产已经部署。

## 注册邮箱查重

- 注册发码接口先查询已有用户（包括停用账号），按邮箱大小写不敏感匹配；已注册返回 HTTP 409 / email_exists，提示直接登录或找回密码，不发送邮件、不生成验证码、不推进邮件轮换。
- 提交注册时先检查邮箱和账号冲突，再校验验证码，避免重复账号消耗验证码。并发注册由已有 users 唯一约束兜底，冲突返回 409，而不是直接抛出数据库唯一约束错误。
- 找回密码发码不受注册查重限制。这里按产品要求明确提示邮箱已注册，因此不是隐藏账号存在性的防枚举响应。
- 本次不新增 Secret 或数据库结构；沿用 schema.sql 已有用户账号和邮箱唯一约束。部署 Worker 后生效，前端现有错误处理会显示服务端 message。线上数据库是否与 schema.sql 一致仍需在部署环境确认。
- 回归测试通过真实 Worker 路由配合本地 SQLite 和模拟邮件请求，覆盖重复邮箱、大小写/输入空格、停用账号、重复账号、正常注册、找回密码发码、并发冲突。不会向真实邮箱发送测试邮件。

## 易支付测试版

支付配置、增量迁移、发布顺序与真实验收见 `EZFPY_CF_SETUP.md`。现已向所有已登录用户开放购买 0.19 / 0.29 元的 Pro 7 天套餐；不再使用测试邮箱白名单。不要将全量 schema.sql 或本地烟测 SQL 导入现有生产库。
