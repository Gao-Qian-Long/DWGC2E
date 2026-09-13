# DWGC2E 对接交付清单（2026-09）

## 已在 APP 本地完成并通过测试
- 默认 `apiMode=worker`，API 为 `https://api.cad.pocketter.dpdns.org`；只有明确配置 `direct` 才读取本地模型密钥。
- 会话令牌使用 DPAPI 本地保存；账号、套餐、额度通过 Worker 获取。
- 图纸解析、术语管理、格式恢复和写回仍在 Windows APP；官网不承担 CAD 翻译。
- `GlossaryFirst=true` 时，APP 对方向匹配的精确术语在本地替换，不调用模型；同优先级冲突明确失败，不静默猜测；未命中术语才随相关提示提交 Worker。
- 本地测试：`dotnet test tests/DwgTranslator.Core.Tests/DwgTranslator.Core.Tests.csproj --no-restore`，当前 67/67 通过；构建 0 警告、0 错误。

## CF 控制台配置（免费额度范围内）
1. Pages 项目：绑定官网 GitHub 仓库 `main`，构建输出使用仓库根目录（纯静态，无构建命令）。
2. Worker：部署 `cf-worker`，绑定 D1 `DB -> dwg-translator-prod`。
3. Worker Custom Domain：`api.cad.pocketter.dpdns.org`，官网变量 `apiBaseUrl` 与 APP `apiBaseUrl` 保持完全一致。
4. Variables：`APP_NAME=DWGC2E`、`ENVIRONMENT=production`、`CORS_ORIGINS=https://cad.pocketter.dpdns.org`、`DEFAULT_PLAN=free`、额度/长度/会话参数、`MAIL_FROM`。
5. Secrets：`PASSWORD_PEPPER`、`DEEPSEEK_API_KEY`、`BREVO_API_KEY`。密钥只能放 Worker，不进 Git、Pages 前端或 APP 安装包。
6. DNS 由 Custom Domain 自动管理；不要同时配置冲突的 Worker Route、Pages 自定义域和通配符。
7. 每次 Worker 代码变更先 `npm install`、`npx wrangler deploy`；Pages 每次官网提交确认部署的是最新 commit，必要时点击重试部署后 Ctrl+F5。

## 部署前必须完成的 Worker 代码工作
- 设备：APP 使用 `%APPDATA%\\DwgTranslator\\device-id` 持久化随机 UUID，设备名仅作展示；Worker 按 `user_id + device_id` 绑定，检查 `revoked`，超限返回 403 `device_limit`；APP 已接入设备列表和撤销接口，并保护当前设备不可在本机撤销。
- 会员：从 `subscriptions` 读取当前用户有效套餐，不能使用全局 `DEFAULT_PLAN` 代表所有用户。
- 额度：当前 Worker 先预留、成功后按有效结果结算、失败回滚，已覆盖 fetch/非 2xx/响应 JSON/模型 JSON 异常；正式生产仍建议补充请求幂等键，避免客户端重试造成重复上游请求。
- 翻译：校验 JSON 数组、唯一 ID 和返回数量；把保护规则和相关 glossary/context 放进服务端 prompt；不要接受客户端自由 prompt。
- 邮件与密码：确认 Brevo 发件人已验证，限流验证码、验证码哈希/过期/一次性消费；密码使用每用户随机 salt 的密码哈希策略。
- 版本：`/v1/version` 和 `update/latest.json` 必须指向真实安装包，而非官网首页；当前线上 `DOWNLOAD_URL` 仍需改成真实 EXE 地址后重新部署。

## 联调验收顺序
1. `GET https://api.cad.pocketter.dpdns.org/` 返回 `status=ok`。
2. 官网注册（含 Brevo 验证码）→ 登录 → 刷新账户页，确认用户名、套餐、额度来自 API。
3. APP 登录；检查 profile/subscription/usage 和设备上限。
4. 术语命中样本：确认无 `/v1/translate` 请求；未命中样本：确认 Worker 返回并写回新文件，原 DWG 不覆盖。
5. 断网、401、402、429、503、额度不足、设备超限分别确认中文提示和可重试行为。
6. 新版本清单下载真实安装包，做升级回滚演练。

## 费用边界
Cloudflare Pages、Workers、D1 在免费额度范围内可运行，但不是无限免费；Brevo、模型 API、域名和下载平台可能有独立配额/收费。达到限额后应看用量，不要自动切换付费服务。


