# DWGC2E Cloudflare 配置清单

> 适用日期：2026-09-13。官网是 Pages 静态站，业务 API 是 Worker，数据是 D1。

## 一、资源

| 资源 | 名称/地址 | 说明 |
|---|---|---|
| Pages | `dwgc2e-website` | 官网静态页面，连接 GitHub `Gao-Qian-Long/DWGC2E_Website` 的 `main` |
| Worker | `dwgc2e-api` | 认证、额度、设备、翻译代理 |
| Worker 域名 | `https://dwgc2e-api.maplehousezz.workers.dev` | 当前已部署并可用；自定义域名待绑定后再切换 |
| D1 | `dwg-translator-prod` | 用户、会话、订阅、额度、设备 |

## 二、Worker 绑定

在 Worker 的 Settings → Bindings 中确认：

```text
D1 Database binding
Variable name: DB
Database: dwg-translator-prod
```

不要把 D1 绑定到 Pages；Pages 只托管官网。

## 三、Worker Variables

在 Settings → Variables and Secrets → Variables 中添加：

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
```

`CORS_ORIGINS` 只填官网正式域名，不要填 `*`。

## 四、Worker Secrets

在同一页面选择 Encrypt / Secret 添加：

```text
PASSWORD_PEPPER=<随机长字符串>
DEEPSEEK_API_KEY=<DeepSeek 密钥>
BREVO_API_KEY=<Brevo API 密钥>
JWT_SECRET=<随机长字符串>
```

这些值只能存在 Worker Secrets：

- 不要写入 GitHub；
- 不要写入 Pages Variables；
- 不要写入 APP 配置文件；
- 不要写入前端 JavaScript；
- 不要放入安装包。

## 五、Brevo 邮件

必须满足：

1. Brevo 中验证发件人地址；
2. 发件人地址与 `MAIL_FROM` 中的邮箱一致；
3. Worker Secret 中存在 `BREVO_API_KEY`；
4. 注册验证码接口返回成功后，再检查收件箱和垃圾邮件；
5. 不要把 Brevo API Key 放到官网。

验证码接口：

```text
POST /v1/auth/register/request-code
POST /v1/auth/password/request-code
```

## 六、Pages 配置

```text
仓库：Gao-Qian-Long/DWGC2E_Website
生产分支：main
构建命令：留空
构建输出目录：/
根目录：仓库根目录
```

官网中的 API 地址必须统一为：`https://dwgc2e-api.maplehousezz.workers.dev`。自定义域名验证成功后才改为 `https://api.cad.pocketter.dpdns.org`

如果 Pages 显示旧页面：

1. 确认 GitHub `main` 已有最新提交；
2. 查看 Pages 部署的 commit 是否一致；
3. 点击 Retry deployment；
4. 浏览器使用 Ctrl+F5 强制刷新。

## 七、本地验证与正式部署

Worker：

```powershell
cd D:\DWGC2E\cf-worker
npm install
npx wrangler login
npx wrangler deploy --dry-run
npx wrangler deploy
```

APP：

```powershell
cd D:\DWGC2E
dotnet build DwgTranslator.sln --no-restore
dotnet test tests/DwgTranslator.Core.Tests/DwgTranslator.Core.Tests.csproj --no-restore --logger "console;verbosity=minimal"
```

正式部署前预期：

```text
APP build：0 errors，0 warnings
Tests：67 passed，0 failed
Worker dry-run：通过
```

## 八、联调顺序

1. `GET https://dwgc2e-api.maplehousezz.workers.dev/` 返回 `status=ok`；
2. 注册邮箱验证码；
3. 注册新账号；
4. 登录并刷新账户页面；
5. 找回密码并确认旧会话失效；
6. APP 登录；
7. 检查账户、套餐、额度和设备；
8. 测试设备绑定、设备上限和设备撤销；
9. 测试术语命中时不调用 `/v1/translate`；
10. 测试未命中术语时调用 `/v1/translate`；
11. 测试额度不足、401、429、503 和断网提示。

## 九、不要做的操作

- 不要执行 `git add .`；
- 不要把 `.wrangler/` 和 `node_modules/` 提交到 Git；
- 不要直接用完整 `schema.sql` 覆盖生产 D1；
- 不要在 APP 中填写 DeepSeek 或 Brevo 密钥；
- 不要同时配置冲突的 Worker Route 和 Custom Domain；
- 不要把 `download_url` 保持为官网首页后就宣称自动更新已完成。

## 十、当前待补项

- `/v1/version` 的真实 EXE 下载地址（待确定安装包托管地址）；
- 生产环境完整线上联调；
- 后续的请求幂等键和非破坏性 D1 迁移。

## 十一、当前本地与线上验证状态（2026-09-13）

- APP 账户中心已读取 `/v1/profile`、`/v1/subscription`、`/v1/usage`、`/v1/devices`。
- 设备移除已接入 `/v1/devices/revoke`，当前设备禁止本机误撤销。
- Worker 版本信息已支持 `LATEST_VERSION`、`DOWNLOAD_URL`、`BACKUP_DOWNLOAD_URL`、`RELEASE_NOTES` Variables。
- 本地 APP build：0 errors，0 warnings。
- 本地测试：67 passed，0 failed。
- Worker dry-run：通过，已确认 `DB` 绑定和变量均能被 Wrangler 识别。
- 线上健康检查：`GET /` 返回 HTTP 200，`GET /v1/version` 返回 HTTP 200。
- 线上 `/v1/version` 当前仍返回旧的 `download_url`（官网地址），必须在 CF Worker Variables 中改成真实 EXE 下载地址后重新部署。
- 以上线上检查不等于已完成真实账号、邮件、额度和翻译联调。




## 十二、Worker 公共契约冒烟检查

无需测试账号、不会发送验证码，也不会修改 D1：

```powershell
cd D:\DWGC2E
tools\Test-WorkerContract.ps1
```

它会检查健康接口、版本接口，以及登录保护接口是否拒绝未认证请求。该脚本不能代替真实账号、Brevo 邮件、设备和额度联调。

## 十二、自动检查

不读取 Secret 值、不发送邮件、不修改 D1：

`powershell
cd D:\DWGC2E.\tools\Test-CloudflareSetup.ps1`r
` 

脚本会检查当前 Worker 健康状态、版本接口和四个必需 Secret 的名称。

## 2026-09-13 对齐修正

当前 Worker 已确认可用地址为 `https://dwgc2e-api.maplehousezz.workers.dev`。在 Worker 自定义域名 `api.cad.pocketter.dpdns.org` 尚未成功绑定前，官网和 APP 应使用该 workers.dev 地址；只有自定义域名绑定并验证成功后，才可切换。

官网 `D:\DWGC2E_Website\js\site-config.js` 与 APP 默认 `AppConfig.ApiBaseUrl` 已对齐到当前 Worker 地址。Pages 只需要部署静态文件，不配置 D1 或密钥。

### 真实验收顺序

1. `GET /`、`GET /v1/version` 匿名访问。
2. Brevo 发件人验证完成后，测试注册验证码和找回密码验证码。
3. 用真实邮箱注册、登录，验证 profile/subscription/usage/devices。
4. 用 APP 登录，验证术语库手动上传/下载、设备绑定和翻译额度。
5. 更新 `DOWNLOAD_URL` 后，关闭旧进程并运行 `D:\DWGC2E\release\DwgTranslator.exe`。

### 尚未能由静态检查证明的项目

真实邮箱送达、真实账号完整流程、额度扣减、设备上限、DeepSeek 实际返回、APP 术语同步和 Pages 最新 commit 部署，必须在云端用真实账号继续验证，不能仅凭匿名接口测试宣称完成。



