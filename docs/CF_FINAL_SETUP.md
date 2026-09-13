# DWGC2E 生产部署清单（最终版）

更新时间：2026-09-13

本文是上线前唯一需要照做的 CF 清单。APP 不需要填写 DeepSeek、Brevo 或 JWT 密钥。

## 1. 资源关系

| 资源 | 名称/地址 | 用途 |
|---|---|---|
| Cloudflare Pages | `cad.pocketter.dpdns.org` | 静态官网 |
| Cloudflare Worker | `dwgc2e-api` | 认证、账户、设备、额度、术语同步、翻译代理 |
| Worker API | `https://dwgc2e-api.maplehousezz.workers.dev` | 当前 APP/官网统一 API |
| D1 | `dwg-translator-prod` | 用户、会话、设备、额度、术语数据 |
| Brevo | 已验证发件人 | 注册/找回密码验证码 |

## 2. Worker → D1 绑定

Cloudflare Dashboard → Workers & Pages → `dwgc2e-api` → Settings → Bindings：

```text
Type: D1 database
Variable name: DB
Database: dwg-translator-prod
```

D1 不绑定到 Pages。

## 3. Worker Variables（普通变量）

```text
APP_NAME=DWGC2E
ENVIRONMENT=production
CORS_ORIGINS=https://cad.pocketter.dpdns.org
DEFAULT_PLAN=free
MAX_TRANSLATE_ITEMS=100
MAX_TEXT_LENGTH=2000
SESSION_TTL_DAYS=30
DEVICE_PLATFORM=Windows
MAIL_FROM=DWGC2E <noreply@mail.cad.pocketter.dpdns.org>
LATEST_VERSION=2.1.0
DOWNLOAD_URL=<最新 EXE/安装包的公开直链>
BACKUP_DOWNLOAD_URL=<可选备用下载直链>
RELEASE_NOTES=<可选版本说明>
```

`CORS_ORIGINS` 不要填 `*`。`DOWNLOAD_URL` 为空时，版本检查仍可用，但自动下载/更新不可用。

## 4. Worker Secrets（加密 Secret）

必须存在且只能配置在 Worker：

```text
PASSWORD_PEPPER
JWT_SECRET
DEEPSEEK_API_KEY
BREVO_API_KEY
```

禁止写入 APP、Pages Variables、GitHub、`wrangler.toml`、前端 JS 或安装包。双平台方案额外保留 `RESEND_API_KEY`，不要删除已经可用的 `BREVO_API_KEY`。
普通变量使用 `MAIL_PROVIDER=round_robin`、`MAIL_FALLBACK_ENABLED=true`：Brevo 和 Resend 轮流作为首选，失败时允许备用。升级前先执行 `cf-worker/migrations/0001_mail_round_robin.sql` 增量建表（不清空用户数据），再部署。详见 cf-worker/README.md 的双平台升级与验收步骤。

## 5. Brevo

1. 在 Brevo 验证发件人邮箱/域名；
2. 发件人邮箱必须与 `MAIL_FROM` 中的邮箱一致；
3. 将 API Key 保存为 Worker Secret `BREVO_API_KEY`；
4. 重新部署 Worker；
5. 用真实邮箱测试：
   - `POST /v1/auth/register/request-code`
   - `POST /v1/auth/password/request-code`

## 6. Pages

```text
Repository: Gao-Qian-Long/DWGC2E_Website
Production branch: main
Build command: 留空
Build output directory: /
Root directory: /
```

Pages 不配置 D1、DeepSeek、Brevo、JWT 或密码 Pepper。官网代码中的 API 地址必须是：

```text
https://dwgc2e-api.maplehousezz.workers.dev
```

## 7. APP

验收只打开：

```text
D:\DWGC2E\release\DwgTranslator.exe
```

APP 默认 API 地址已经是 Worker 地址，并且生产代码强制走 Worker。不要在 APP 中填写任何供应商密钥。

## 8. 部署命令

```powershell
cd D:\DWGC2E\cf-worker
npm install
npx wrangler login
npx wrangler deploy
```

修改 Variables/Secrets 后必须重新部署 Worker。Pages 修改 `main` 后需确认部署使用了最新 commit。

## 9. 验收顺序

1. `GET /` 返回 `status=ok`；
2. `GET /v1/version` 返回当前版本；
3. 真实邮箱收到注册验证码；
4. 注册、登录；
5. 官网账户中心读取 profile/subscription/usage/devices；
6. APP 登录并绑定设备；
7. 设备上限和撤销；
8. APP 本地术语库新增/编辑/文件夹管理；
9. 手动上传和下载云端术语库（云端最多 1000 条）；
10. Worker 代理翻译并扣减额度；
11. 找回密码后旧会话失效；
12. 设置 `DOWNLOAD_URL` 后测试下载。

## 10. 本地检查命令

```powershell
cd D:\DWGC2E
.\tools\Test-WorkerContract.ps1 -BaseUrl https://dwgc2e-api.maplehousezz.workers.dev
.\tools\Test-CloudflareSetup.ps1
 dotnet build DwgTranslator.sln --no-restore
 dotnet test DwgTranslator.sln --no-build --no-restore
```

匿名接口检查出现 `401` 是正确结果，表示登录保护生效；它不能替代真实邮箱和真实账号测试。

## 11. 当前未完成项

截至 2026-09-13，代码和匿名线上检查已完成；仍需真实环境确认：真实邮件送达、真实账号完整流程、APP 云端联调、额度扣减、DeepSeek 实际响应、Pages 最新部署和 `DOWNLOAD_URL` 下载。
