# DWGC2E 发布与 CF 对接运行手册

适用日期：2026-09-13

## 1. 三端职责

- Windows APP：DWG/DXF 解析、术语库、AI 翻译、CAD 写回。
- Cloudflare Pages：官网、下载、注册登录、账户展示。
- Cloudflare Worker + D1：认证、验证码邮件、会话、套餐、额度、设备和 AI 代理。

官网不执行真实图纸翻译，也不承载 APP 术语库。

## 2. CF Worker 必填项

Worker：`dwgc2e-api`
D1：`dwg-translator-prod`
D1 binding：`DB`
Worker 域名：`https://api.cad.pocketter.dpdns.org`

Variables：

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
DOWNLOAD_URL=<真实 EXE 下载直链>
BACKUP_DOWNLOAD_URL=<备用下载直链，可留空>
RELEASE_NOTES=<版本说明，可留空>
```

Secrets（仅 Worker Secrets）：

```text
PASSWORD_PEPPER
BREVO_API_KEY
DEEPSEEK_API_KEY
```

密钥不能放入 APP、Pages Variables、前端 JS、GitHub 或安装包。

## 3. Pages 必填项

```text
仓库：Gao-Qian-Long/DWGC2E_Website
生产分支：main
根目录：/
构建命令：留空
输出目录：/
```

官网 API 地址必须是：

```text
https://api.cad.pocketter.dpdns.org
```

## 4. APP 发布验收文件

用户验收入口固定为：

```text
D:\DWGC2E\release\DwgTranslator.exe
```

不要只运行 Debug 输出，也不要用旧桌面快捷方式判断页面是否更新。

发布命令：

```powershell
cd D:\DWGC2E
cmd /c publish.bat
```

脚本会生成并同步：

```text
release\DwgTranslator.exe
release\settings.json
release\CadPlugin\*
release\glossaries\*
release\prompts\*
```

运行发布前必须关闭正在运行的 `DwgTranslator.exe`，否则 Windows 会锁定 EXE，导致同步失败。

## 5. 验证顺序

```powershell
cd D:\DWGC2E
dotnet build DwgTranslator.sln --no-restore
dotnet test tests\DwgTranslator.Core.Tests\DwgTranslator.Core.Tests.csproj --no-restore
npx --prefix cf-worker wrangler deploy --dry-run
tools\Test-WorkerContract.ps1
tools\Test-CfApi.ps1
```

然后使用真实测试邮箱依次验证：

1. 注册验证码；
2. 注册和重复账号拦截；
3. 登录；
4. 找回密码；
5. APP 登录与会话过期；
6. 套餐、额度和设备数量；
7. 其他设备撤销；
8. 术语命中不消耗 AI 额度；
9. 未命中术语调用 Worker 翻译；
10. 额度不足、401、429、503 和断网提示。

本地测试或未认证 401 检查不能替代真实邮箱和账号联调。

## 6. 部署注意事项

- 修改 APP 后必须重新执行 `publish.bat`，再打开 `release` 下的 EXE。
- 修改官网后推送 `main`，确认 Pages 部署 commit 与 GitHub 一致。
- 修改 Worker 后重新部署，并再次运行两个 CF 检查脚本。
- `DOWNLOAD_URL` 未填写真实 EXE 地址前，不要宣称自动更新和官网下载安装已经完成。
- 不要提交 `cf-worker\node_modules`、`cf-worker\.wrangler` 和截图缓存。
