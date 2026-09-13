# DWGC2E Cloudflare 配置清单

更新时间：2026-09-13

## 当前实际地址

- Pages 官网：`https://cad.pocketter.dpdns.org`
- Worker API（当前可用）：`https://dwgc2e-api.maplehousezz.workers.dev`
- D1 数据库：`dwg-translator-prod`
- Pages 部署分支：`main`
- APP 本地开发分支：`pro`

> 当前官网和 APP 默认已经改为使用上面的 `workers.dev` 地址。只有在 Cloudflare Worker 中成功绑定自定义域名后，才能改成 `api.cad.pocketter.dpdns.org`。

## CF 必须配置

### 1. D1

Worker 的 D1 binding 必须为：

- Binding：`DB`
- Database name：`dwg-translator-prod`
- Database ID：`16fa90f6-ddc8-4341-a963-4e66fd902c63`

数据库需要执行 `schema.sql`。不要重复创建同名表；更新表结构时使用迁移 SQL。

### 2. Worker Secrets

在 Worker 的 Settings → Variables and Secrets 中配置为 Secret：

- `PASSWORD_PEPPER`
- `JWT_SECRET`
- `DEEPSEEK_API_KEY`
- `BREVO_API_KEY`

可选兼容项：

- `ADMIN_API_KEY`
- `RESEND_API_KEY`（旧邮件方案，使用 Brevo 后可删除，但先确认线上不再依赖）

这些值不能放进 APP、Pages 静态文件或 GitHub。

### 3. Worker Variables

公开变量可以放在 Wrangler `[vars]` 中：

- `APP_NAME=DWGC2E`
- `ENVIRONMENT=production`
- `CORS_ORIGINS=https://cad.pocketter.dpdns.org`
- `DEFAULT_PLAN=free`
- `MAX_TRANSLATE_ITEMS=100`
- `MAX_TEXT_LENGTH=2000`
- `SESSION_TTL_DAYS=30`
- `DEVICE_PLATFORM=Windows`
- `MAIL_FROM=DWGC2E <noreply@mail.cad.pocketter.dpdns.org>`
- `LATEST_VERSION=2.1.0`
- `DOWNLOAD_URL`：填写最新 EXE 的公开直链
- `BACKUP_DOWNLOAD_URL`：可选备用下载地址
- `RELEASE_NOTES`：版本说明

### 4. Brevo 邮件

Brevo 中必须完成发件人验证，并保证发件人地址与 `MAIL_FROM` 的邮箱一致。当前验证码接口：

- `POST /v1/auth/register/request-code`
- `POST /v1/auth/password/request-code`

验证域名时按 Brevo 控制台要求添加 DNS 记录。验证码实际发送由 Worker 调用 Brevo API 完成，不需要在 Pages 配置 API Key。

### 5. CORS

Worker 的 `CORS_ORIGINS` 至少包含：

`https://cad.pocketter.dpdns.org`

如果本地调试，再临时增加本地来源；发布后应删除不需要的来源。APP 不依赖浏览器 CORS。

### 6. Pages

Pages 项目：

- 构建方式：静态站点，无构建命令
- 输出目录：仓库根目录
- 生产分支：`main`
- 不需要配置 Worker Secret

Pages 使用 `js/site-config.js` 中的 Worker 地址。修改官网后必须提交并推送 `main`，然后在 Pages 中确认部署的 commit SHA。

### 7. 版本下载

将 `DOWNLOAD_URL` 指向可以直接下载 EXE/安装包的地址。蓝奏云分享页不一定是直接文件下载地址，若 APP 自动更新无法识别，应提供稳定的直链或 Pages 静态更新清单：

`https://cad.pocketter.dpdns.org/update/latest.json`

## 当前已实现接口

认证、账户、套餐读取、额度读取、设备绑定/撤销、术语库云端手动同步、版本检查和 Worker 翻译代理已经有代码。术语库默认只存在 APP 本地，云端同步必须由用户主动触发，最多 1000 条。

官网只负责宣传、下载、登录、套餐、额度和设备信息；DWG/DXF 解析、术语匹配、AI 翻译和 CAD 写回仍由 Windows APP 完成。

## 每次部署后的验证顺序

```powershell
# Worker
npx wrangler deploy

# 匿名接口
Invoke-RestMethod https://dwgc2e-api.maplehousezz.workers.dev/
Invoke-RestMethod https://dwgc2e-api.maplehousezz.workers.dev/v1/version

# APP 构建与发布
cd D:\DWGC2E
dotnet build DwgTranslator.sln --no-restore
dotnet test DwgTranslator.sln --no-build --no-restore
.\publish.bat
```

然后使用真实邮箱验证：注册验证码 → 注册 → 登录 → 账户信息/设备 → 找回密码 → 新密码登录。最后关闭旧进程，只打开 `D:\DWGC2E\release\DwgTranslator.exe` 验收。

## 目前仍需真实线上验证

- Brevo 验证码是否实际到达收件箱
- 真实账号登录后 APP 的 Worker 模式翻译
- 额度扣减和额度不足
- 设备上限与撤销后的重新绑定
- APP 术语库上传/下载
- 最新 EXE 下载地址和 Pages 自动部署
