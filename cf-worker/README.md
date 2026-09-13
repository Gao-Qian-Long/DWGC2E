# DWGC2E Cloudflare Worker

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
- `MAIL_FROM = "DWGC2E <noreply@mail.cad.pocketter.dpdns.org>"`
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



## 双邮件平台（Brevo + Resend）

保留已经可用的 Brevo，在同一个 Worker 添加 Secret `RESEND_API_KEY`。无需新建 Worker、修改 D1 或重配已验证的 DNS。两个平台均须允许 MAIL_FROM 对应的发件人。

普通变量（已写入 wrangler.toml）：

- MAIL_PROVIDER=brevo：首选 Brevo；设为 resend 可首选 Resend。
- MAIL_FALLBACK_ENABLED=true：允许备用平台；false 则只使用所选平台。

若主平台未配置密钥且允许备用，会直接使用有密钥的备用平台；配置值非法会报邮箱未配置，不会盲目发送。
主平台返回 401/402/403/429/5xx 时尝试备用平台一次；400/422 等请求错误不会切换。两次发送共用同一条验证码数据库记录和邮件内容，失效时间不重置。
网络异常/超时意味着发送结果不确定，不自动跨平台重发，提示检查邮箱并保留验证码有效。5xx 下也无法严格保证不重复投递，可能收到内容相同的两封邮件。API 接受请求不等于收件箱送达。

### 部署与验收

1. CF → dwgc2e-api → Settings → Variables and Secrets：添加加密 RESEND_API_KEY，保留 BREVO_API_KEY 和 MAIL_FROM。勿把密钥写入 Git。
2. 本地运行 npm ci、npm test、npm run typecheck，随后 npx wrangler deploy。Dashboard 手动更改的普通变量可能被本地 wrangler.toml 覆盖，部署前核对；不要重新执行生产 schema。
3. 验证 Brevo：临时 MAIL_PROVIDER=brevo、MAIL_FALLBACK_ENABLED=false，保存并部署，申请一次验证码并检查 Brevo 发信记录和收件箱。
4. 验证 Resend：临时 MAIL_PROVIDER=resend、MAIL_FALLBACK_ENABLED=false，保存并部署，申请验证码并检查 Resend 发信记录和收件箱。避免频繁点击触发限流。不要用删除密钥的方式测试。
5. 验收后恢复 MAIL_PROVIDER=brevo、MAIL_FALLBACK_ENABLED=true 并部署。

Worker 日志中的 verification_mail 包含 provider、HTTP status 和 request_id，不包含邮箱、验证码或密钥。成功接收响应只证明平台接受请求；实际送达仍需人工查收。单元测试使用模拟请求，不会发真实邮件。
