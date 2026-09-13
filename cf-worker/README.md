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
npx wrangler secret put DEEPSEEK_API_KEY
npx wrangler secret put BREVO_API_KEY
```

- `PASSWORD_PEPPER`：固定的随机长字符串；以后不要更换，否则历史密码哈希无法验证。
- `DEEPSEEK_API_KEY`：只放 Worker，用于服务端翻译，不下发到 APP。
- `BREVO_API_KEY`：只放 Worker，用于注册/找回密码验证码邮件。

## wrangler.toml 中的公开变量

当前仓库已经提供默认值，重点确认：

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
.\tools\Test-WorkerContract.ps1
```

重点验证：注册/登录、验证码邮件、找回密码、额度、设备、`/v1/glossary`。未登录访问 `/v1/glossary` 应返回 `401`；登录后 GET/PUT 术语库最多 1000 条。
