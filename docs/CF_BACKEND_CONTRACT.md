# DWGC2E 后端对接清单（Cloudflare 侧）

> 客户端已按本文档实现接口抽象（`IApiClient` / `Core/Api/ApiContracts.cs`）。
> 当前已实现接口抽象与 Worker HTTP 客户端；桌面翻译入口仍显式阻止 Worker 模式。
> 后端实现下列端点后，还需接通任务层适配器、登录令牌流程和端到端验证；仅修改 apiMode/apiBaseUrl 尚不能启动 Worker 翻译。

最终链路：

```text
DWGC2E.exe ──HTTPS──► Cloudflare Worker ──► D1（账号/会员/额度/设备）
                                   └──────► AI Gateway ──► DeepSeek
```

正式 Worker 模式的 DeepSeek Key 仅保存在 Worker。现有 direct 模式仍读取本地用户配置的 Key；两种模式应分别验收。

---

## 一、必须实现的端点

统一前缀 `/v1`，全部 `Content-Type: application/json; charset=utf-8`，全部支持 `Authorization: Bearer <token>`（除 login/version）。

| # | 方法与路径 | 用途 | 请求体 | 响应体 |
|---|---|---|---|---|
| 1 | `POST /v1/auth/login` | 登录 | `{ "account": "...", "password": "...", "device_id": "...", "device_name": "..." }` | `LoginResult` |
| 2 | `GET /v1/profile` | 账户信息 | — | `ProfileInfo` |
| 3 | `GET /v1/subscription` | 套餐与权益 | — | `SubscriptionInfo` |
| 4 | `GET /v1/usage` | 用量与额度 | — | `UsageInfo` |
| 5 | `POST /v1/translate` | **核心**翻译 | `TranslationBatchRequest` | `TranslationBatchResult` |
| 6 | `POST /v1/devices/bind` | 绑定/更新设备 | `{ "device_id": "...", "device_name": "..." }` | `DeviceBindResult` |
| 7 | `GET /v1/version?current=2.1.0` | 版本检查 | — | `VersionInfo` |
| 8 | `GET /update/latest.json` | 静态更新清单（可放 Pages/KV，不必走 Worker） | — | 见 §五 |

客户端调用点：`Core/Api/ApiContracts.cs`（7 个方法，一一对应）。

---

## 二、数据结构（与客户端 DTO 字段名严格一致，线上字段使用下列 snake_case；大小写不敏感不等于支持 camelCase）

### 翻译请求（客户端 → Worker）§二十一

```json
{
  "source_lang": "ZH",
  "target_lang": "EN",
  "protection": {
    "protect_dimensions": true,
    "protect_tolerances": true,
    "protect_models": true,
    "glossary_first": true
  },
  "glossary": [ { "source": "表面粗糙度", "target": "Surface Roughness" } ],
  "items": [
    { "id": 1, "text": "表面粗糙度", "height": 3.5, "rotation": 0, "layer": "TEXT", "context": "WD_M" }
  ]
}
```

**关键点**：客户端**不允许**发送自由 prompt ✗ —— prompt、模型、温度、工程规则全部由 Worker 掌控 ✓。

### 翻译响应（Worker → 客户端）

```json
{
  "success": true,
  "characters_used": 1_284,
  "cached_count": 37,
  "items": [
    { "id": 1, "translated_text": "Surface Roughness", "from_cache": false },
    { "id": 2, "error_code": "quota_exceeded" }
  ]
}
```

- `items[].translated_text` 为空 + `error_code` 有值时，客户端把该条标为失败但**不终止整张图纸** ✓
- `characters_used` 由 Worker 统计（源文本字符数或 token 折算，二者选一固定即可）✓

### 登录 / 账户 / 套餐 / 额度 / 设备 / 版本

```json
// LoginResult
{ "success": true, "token": "jwt-or-opaque", "expires_at": "2026-01-15T00:00:00Z" }
{ "success": false, "error_code": "device_limit", "message": "设备数量已达到套餐上限" }

// ProfileInfo
{ "display_name": "张工", "email": "zhang.gong@company.com", "is_active": true }

// SubscriptionInfo
{ "plan_name": "专业版", "starts_at": "2025-01-15T00:00:00Z", "expires_at": "2026-01-15T00:00:00Z",
  "auto_renew": true, "entitlements": ["批量任务", "专业术语库", "更高并发", "3 台设备"] }

// UsageInfo
{ "monthly_quota": 1000000, "used": 176579, "reset_at": "2026-01-01T00:00:00Z" }

// DeviceBindResult
{ "success": true, "used_devices": 2, "max_devices": 3 }

// VersionInfo
{ "latest_version": "2.2.0", "download_url": "https://.../DWGC2E-2.2.0-setup.exe",
  "backup_download_url": "https://github.com/.../releases/download/v2.2.0/...",
  "release_notes": "- 修复...\n- 新增...", "mandatory": false }
```

---

## 三、错误码约定（客户端已内置中文映射，见 `ApiErrorMessages`）

| error_code | 客户端显示 |
|---|---|
| `unauthenticated` / `token_expired` | 登录已过期，请重新登录 |
| `subscription_expired` | 会员已到期 |
| `quota_exceeded` | 本月翻译额度不足 |
| `device_limit` | 设备数量已达到套餐上限 |
| `rate_limited` | 请求过于频繁，正在排队重试 |
| `upstream_unavailable` | 翻译服务暂时不可用，请稍后再试 |
| `invalid_drawing` | 无法解析该 DWG 文件 |
| `file_locked` | 文件正在被其他程序占用 |
| `output_exists` | 输出文件已存在 |
| `cad_version_unsupported` | 当前 CAD 版本暂不支持 |

HTTP 状态码：`400` 参数错 / `401` 未认证或令牌过期 / `402` 额度或会员问题 / `403` 设备超限 / `429` 限流 / `503` 上游不可用。响应体始终带 `error_code` + `message`。

---

## 四、D1 表结构（建议，按你现有习惯可调，字段名保持语义即可）

```sql
-- 账号
CREATE TABLE users (
  id            TEXT PRIMARY KEY,          -- uuid
  account       TEXT UNIQUE NOT NULL,      -- 邮箱或手机号
  password_hash TEXT NOT NULL,             -- argon2id / bcrypt，禁止明文
  display_name  TEXT,
  email         TEXT,
  created_at    TEXT NOT NULL,
  is_active     INTEGER NOT NULL DEFAULT 1
);

-- 会员
CREATE TABLE subscriptions (
  user_id     TEXT PRIMARY KEY REFERENCES users(id),
  plan_name   TEXT NOT NULL,               -- 免费版 / 专业版 / 企业版
  starts_at   TEXT, expires_at TEXT,
  auto_renew  INTEGER DEFAULT 0,
  updated_at  TEXT NOT NULL
);

-- 月度额度与用量（按自然月一条）
CREATE TABLE usage_monthly (
  user_id      TEXT NOT NULL,
  year_month   TEXT NOT NULL,              -- 2026-01
  chars_used   INTEGER NOT NULL DEFAULT 0,
  chars_quota  INTEGER NOT NULL DEFAULT 1000000,
  task_count   INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (user_id, year_month)
);

-- 设备（套餐设备数上限从这里算）
CREATE TABLE devices (
  device_id   TEXT PRIMARY KEY,            -- 客户端 MachineIdentifier 派生值
  user_id     TEXT NOT NULL REFERENCES users(id),
  device_name TEXT,
  platform    TEXT DEFAULT 'Windows',
  first_seen  TEXT, last_seen TEXT,
  revoked     INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX idx_devices_user ON devices(user_id);

-- 订单
CREATE TABLE orders (
  order_no   TEXT PRIMARY KEY,
  user_id    TEXT NOT NULL,
  plan_name  TEXT, amount_cents INTEGER, currency TEXT DEFAULT 'CNY',
  status     TEXT,                         -- pending / paid / cancelled / refunded
  created_at TEXT NOT NULL, paid_at TEXT
);

-- 翻译用量明细（可选，用于风控与成本核算）
CREATE TABLE usage_logs (
  id TEXT PRIMARY KEY, user_id TEXT, device_id TEXT,
  src_lang TEXT, tgt_lang TEXT, chars INTEGER, cached INTEGER,
  model TEXT, created_at TEXT NOT NULL
);
```

---

## 五、更新清单 `update/latest.json`

建议放 Cloudflare Pages 或 KV（不必走 Worker），客户端默认地址：
`https://cad.pocketter.dpdns.org/update/latest.json`

```json
{
  "latest_version": "2.2.0",
  "download_url": "https://你的蓝奏云直链/setup.exe",
  "backup_download_url": "https://github.com/你的仓库/releases/download/v2.2.0/DWGC2E-2.2.0-setup.exe",
  "release_notes": "- 新增批量任务分组\n- 修复中文图纸导出残留",
  "mandatory": false
}
```

字段与 `VersionInfo` 一一对应（蛇形/驼峰均可）。

---

## 六、Worker 里「值钱的部分」要放哪（§二十二 防破解）

即使 exe 被逆向，也应拿不到完整产品能力。建议把下列内容**只放在 Worker**，客户端不下发：

```text
· System Prompt 与工程语义规则（哪些该译、哪些必须原样保留）
· 尺寸 / 公差 / 型号 / 材料牌号的判定规则细节
· 术语决策与冲突裁决（用户 > 企业 > 系统 > AI 默认）
· 排版参数（字号收缩上限、走廊留边、干涉判定阈值）
· 模型选择、温度、Token 上限
· 会员、额度、设备裁决
```

客户端保留：DWG/DXF 打开、实体遍历、基础读写、把服务端下发的结果写回并做几何放置。

---

## 七、翻译接口的并发与限流建议

- Worker 侧对同一 `user_id` 做**并发上限**（例如 4 个在途请求）+ 每分钟请求数上限 → 返回 `429 rate_limited`
- 对同一 `device_id` 做幂等去重（同一批 `items` 哈希在 30s 内重复提交直接回缓存 ✓）
- 上游（DeepSeek）失败按指数退避重试 2 次，仍失败返回 `503 upstream_unavailable`
- 响应头可带 `X-Chars-Used` 便于客户端对齐计费显示

---

## 八、你在 CF 侧完成后的自检清单

1. `POST /v1/translate` 用 curl 能返回上面结构的 JSON，且 `items[].translated_text` 正确
2. 未带 `Authorization` 时返回 `401` + `error_code=token_expired`（验证客户端提示语 ✓）
3. 额度用尽时返回 `402` + `quota_exceeded`
4. 设备数超限时返回 `403` + `device_limit`
5. `GET /update/latest.json` 可匿名访问
6. Worker 的 Secrets 里有 DeepSeek key（客户端 `settings.json` 里应保持为空 ✓）

## 九、客户端切换方式（你完成后告诉我，我这边一行配置就能切）

`%APPDATA%\DwgTranslator\settings.json`：

```json
{
  "apiMode": "worker",
  "apiBaseUrl": "https://cad-api.你的域名.workers.dev"
}
```

切完客户端仍然只在 `IApiClient` 后面工作，UI 与 CAD 插件完全不需要改动 ✓。

## 本地契约验证（2026-09-13）

命令：dotnet test tests/DwgTranslator.Core.Tests/DwgTranslator.Core.Tests.csproj --no-restore

新增 WorkerApiContractTests 共 8 个用例，使用进程内 HttpMessageHandler，不访问线上服务：
- POST /v1/translate 的路径、Bearer、JSON Content-Type、snake_case 语言/实体/保护字段。
- snake_case 译文、计费字符数与缓存标记反序列化。
- 响应缺少请求 ID 时生成 missing_result。
- 401/402/429/503 无 JSON 响应体时的错误码映射。
- 调用方取消原样传播 OperationCanceledException。
- 每次请求重新读取令牌，登录刷新后不复用旧值。

当前全量结果：42/42 通过。这些测试证明以上传输行为，不证明登录 UI、任务层 Worker 适配器、D1 计费、设备裁决或真实 CAD 写回已接通。
服务端联调前仍需增加完整端点用例、版本清单外部域认证隔离测试和端到端任务链路验证。

## 六、客户端上线前必须核对（2026-09）

APP 新安装默认使用 Worker 模式：

```text
apiMode: worker
apiBaseUrl: https://api.cad.pocketter.dpdns.org
updateManifestUrl: https://cad.pocketter.dpdns.org/update/latest.json
```

不要把 `DEEPSEEK_API_KEY` 写入 APP 的 `settings.json`。正式版本的密钥只能放在 Worker Secret；APP 只保存经过 DPAPI 保护的登录令牌。

### 端到端验收顺序

1. `GET https://api.cad.pocketter.dpdns.org/` 返回 `status: ok`。
2. 用官网注册的账号在 APP 登录；请求必须包含 `device_id` 和 `device_name`。
3. 登录成功后依次检查 `/v1/profile`、`/v1/subscription`、`/v1/usage`，确认头像/套餐/额度来自服务端，不能用本地默认值冒充。
4. 首次登录检查 `/v1/devices/bind` 的结果；服务端必须在达到上限时返回 HTTP 403 和 `device_limit`，不能只返回当前数量。
5. 通过 APP 选择一张测试图纸，先用术语命中样本，再测试未命中样本；确认翻译结果写回本地输出文件，原文件不被覆盖。
6. 额度不足、令牌过期、网络断开、Worker 503 时分别验证中文错误提示和可重试行为。
7. 更新清单可访问且 `download_url` 指向真实安装包，不要指向官网首页。

### Cloudflare 只需配置的项目

- Worker 路由：`api.cad.pocketter.dpdns.org/*`；
- D1 绑定：`DB -> dwg-translator-prod`；
- Worker Variables：`CORS_ORIGINS`、`MAIL_FROM`、额度和设备策略；
- Worker Secrets：`JWT_SECRET`、`PASSWORD_PEPPER`、`DEEPSEEK_API_KEY`、`BREVO_API_KEY`；
- Pages：部署 `main` 分支并放置 `update/latest.json`；
- 不需要在 Pages 配置 D1，APP 和官网都只通过 Worker HTTPS API 访问。

## 术语云端备份（可选）

APP 的术语默认只保存在本地。需要跨电脑备份时使用认证接口：

- `GET /v1/glossary`：读取当前账号的云端术语；
- `PUT /v1/glossary`：保存 `{ entries: [...] }`，最多 1000 条；
- 数据按 `user_id` 隔离，服务端不会公开术语内容；
- 当前 APP UI 尚未默认开启自动云同步，避免未经用户同意上传本地术语。后续增加“上传/下载”明确操作后再接入客户端。
