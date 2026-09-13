# DWGC2E 网页端与 APP 端对接实施方案

日期：2026-09-13

## 0. 当前结论

- 官网仓库：`D:\DWGC2E\_Website\DWGC2E_Website`，当前为纯静态 HTML/CSS/JS，没有 `functions/`、用户系统、支付系统或数据库。
- 官网生产分支：`main`，Cloudflare Pages 应继续挂载 `main`。
- APP 已有 `IApiClient` / `WorkerApiClient` / `WorkerTranslationService` 和结构化翻译 DTO，但 Worker 后端尚未在官网仓库实现。
- 官网与 APP 不能把“官网静态展示”和“在线图纸翻译”混为一个产品：官网负责产品介绍、下载和账户入口；APP 负责本地图纸解析、任务队列、写回和本地历史；云端 API 负责身份、授权、额度和 AI 翻译策略。

## 1. 目标架构

```text
官网（Cloudflare Pages / main）
  ├─ 静态产品页
  ├─ 下载 / 更新清单
  └─ 账户入口（后续调用 API）

APP（WPF） ─HTTPS─> API（Cloudflare Worker 或 Pages Functions）
                         ├─ D1：用户、会员、额度、设备、会话
                         ├─ AI Gateway / DeepSeek：仅服务端密钥
                         └─ R2/KV（可选）：更新清单、缓存
```

第一阶段不做“网页上传 DWG 在线翻译”，避免重复建设 CAD 引擎和文件安全链路。

## 2. API 统一约定

生产 API 基地址建议：`https://cad.pocketter.dpdns.org/v1`。

统一：JSON、UTC 时间、Bearer Token（login/version 除外）、错误响应：

```json
{ "error_code": "quota_exceeded", "message": "本月翻译额度不足", "request_id": "..." }
```

接口：

| 方法 | 路径 | 网页 | APP |
|---|---|---|---|
| POST | `/v1/auth/login` | 账户登录 | 登录并保存会话 |
| POST | `/v1/auth/register` | 后续注册 | 不直接使用 |
| GET | `/v1/profile` | 账户页 | 会员中心 |
| GET | `/v1/subscription` | 账户页 | 会员状态 |
| GET | `/v1/usage` | 账户页 | 额度显示 |
| POST | `/v1/translate` | 不直接调用 | 批量翻译 |
| POST | `/v1/devices/bind` | 不直接调用 | 设备绑定 |
| GET | `/v1/version?current=...` | 下载页可展示 | 自动更新 |
| GET | `/update/latest.json` | 下载页 | 自动更新备用清单 |

网页端不能调用 DeepSeek，不能保存 API Key，不能假装接口已经存在。

## 3. 官网实施步骤（main）

### 3.1 先完成静态官网稳定版

1. 将下载按钮改为真实外部下载地址（蓝奏云或正式下载地址），不能使用不存在的 `/downloads/*.exe` 占位路径。
2. 用 `config.js` 或 `site-config.json` 集中管理版本号、下载地址、更新日志、API 地址。
3. 首页保留产品介绍、功能、支持格式、截图、版本和下载，不增加在线 DWG 上传。
4. 登录/会员按钮在 API 未上线前显示明确的“账户系统准备中”，不使用误导性的“已登录/购买成功”。
5. 增加统一加载、错误、空状态；所有按钮必须有真实跳转或明确反馈。
6. 做 PC Chrome/Edge、移动端、隐私政策、用户协议、SEO 和下载链路验收。
7. 在 API 上线后再把登录/账户页接入 `/v1`，Token 只放 HttpOnly/Secure/SameSite Cookie（网页），不要放 URL。

### 3.2 官网后端接入（API 就绪后）

- Pages Functions 可以承载轻量账户接口；如果翻译请求、限流、队列和密钥管理变复杂，使用独立 Worker，官网仍保留 Pages。
- D1 绑定名固定为 `DB`；生产密钥只放 Cloudflare Secret。
- CORS 只允许 `https://cad.pocketter.dpdns.org`，不允许 `*`。
- 账号密码只存 Argon2id/PBKDF2 哈希；登录限流；返回 opaque session token。
- 购买/支付页面先只做跳转和订单查询，不在前端伪造支付成功。

## 4. APP 实施步骤

### 4.1 API Client

- 删除 UI/任务层对 `DeepSeekClient` 的直接依赖；Worker 模式只走 `IApiClient.TranslateAsync`。
- `ApiBaseUrl` 规范化为 `https://cad.pocketter.dpdns.org`，客户端内部拼 `/v1/...`，避免出现重复 `/v1`。
- 登录成功后加密保存 token；请求自动加 Bearer；过期/401 清会话并提示重新登录。
- 所有失败返回结构化错误码；禁止吞异常；网络错误支持有限退避重试，额度/权限错误不重试。
- 翻译请求使用 `source_lang/target_lang/protection/glossary/items`，按 id 严格匹配响应，缺失/重复结果判失败。

### 4.2 任务与 CAD

- 本地 SQLite 记录任务、文件、条目状态、耗时、错误和恢复信息。
- 状态：等待、解析中、提取文字、翻译中、排版处理中、写回中、完成、失败、已取消。
- 本地任务并发与 AI 并发分开限流；取消使用 CancellationToken；任务重启可恢复等待/失败任务。
- 默认不覆盖源 DWG；输出名采用 `{name}_{lang}`；写回成功后才标记完成。
- 翻译前显示分析统计；翻译后显示保护字段、调整字高、干涉处理和失败数量。

### 4.3 APP UI

五个主页面：图纸翻译、批量任务、术语库、会员中心、设置。

- 每页最多一个蓝色主操作。
- 批量任务支持全选/反选/三态、重试、取消、删除、打开输出目录、查看日志。
- 术语库支持搜索、启用/禁用、新增、编辑、删除、冲突处理和保存反馈。
- 设置分为常规、翻译、文件、性能、账号、关于；关闭叉号与取消统一语义。
- Worker 模式隐藏 DeepSeek Key、模型和直连测试配置。

## 5. 两端对齐检查

### 当前已发现的问题

1. 官网仓库没有后端代码，因此 `/v1/*` 当前不是可用接口。
2. 官网当前下载地址曾被改成 Pages 内不存在的 EXE 路径，必须换成真实外部下载地址。
3. APP 合同使用 `/v1`，历史讨论中又出现 `/api/translate`，必须选一个；本方案统一 `/v1/translate`。
4. APP 当前配置语义必须明确：`ApiBaseUrl` 填域名根，不填带 `/v1` 的路径。
5. APP DTO 的 `TranslationItem.Id` 只在单批次内唯一；服务端不得跨请求混淆，日志需带 request_id。
6. APP 的 Profile/Subscription/Usage DTO 对失败信息表达不足，建议统一增加可选错误结果或由 API Client 返回带状态的结果对象。
7. 官网是静态展示，不能承诺在线处理 DWG；所有宣传文案要和 APP 实际能力一致。

### 对齐验收

- 同一账号网页登录（后续）和 APP 登录返回同一账户状态。
- 会员、额度、设备状态在官网和 APP 显示一致。
- APP 一次翻译请求的字符扣减是服务端原子操作；重试不能重复扣费。
- 服务端返回的每个 item id 都能准确写回原实体。
- 401、会员过期、额度不足、设备超限、限流、上游故障都有一致中文提示。
- 官网下载版本、`latest.json` 和 APP `/v1/version` 的版本号一致。

## 6. 你需要提供/完成的事项

### 现在必须提供

1. 真实软件下载地址（蓝奏云链接或其他正式地址）。
2. 最终产品版本号，例如 `1.0.0`。
3. 是否已有注册/支付系统；如果没有，第一阶段先不上线支付按钮。
4. Cloudflare Pages 项目是否允许新增 Functions；如继续纯静态，API 使用独立 Worker 子域名也可以。

### 你在 Cloudflare 控制台完成

1. Pages 生产分支保持 `main`。
2. 如果采用 Pages Functions，把源码放在官网仓库根目录 `functions/`，不要只在控制台拖拽静态文件。
3. 创建 D1 数据库并提供 database id（不是密码）。
4. 建立 binding：`DB`。
5. 在 Cloudflare Secret 中配置 DeepSeek/AI Gateway 密钥、SESSION_SECRET；不要发给我。
6. 确认 API 使用哪个公开地址：推荐 `https://cad.pocketter.dpdns.org/v1`，或单独的 `https://api.cad.pocketter.dpdns.org/v1`。
7. 如果已有支付/用户系统，提供其公开接口文档和测试账号；不要提供生产密码。

### 我负责

- 官网配置集中化、下载/版本/更新日志和真实状态。
- Pages Functions/Worker API 实现与接口测试。
- D1 schema/migration。
- APP API Client 对接、登录会话、额度/会员/设备状态。
- 任务队列、重试、恢复、UI 状态和错误映射。
- 两端联调和构建验收。

## 7. 实施顺序

1. 锁定官网稳定版本并修复真实下载配置。
2. 在 `main` 添加 API 契约测试、Functions/Worker 骨架和 D1 migration。
3. 先实现 health/version/login/profile/usage/subscription/device bind。
4. 再实现 translate、原子额度扣减、缓存和限流。
5. APP 切换 Worker 模式并完成登录、翻译、错误、恢复联调。
6. 官网接入账户查询和版本信息。
7. 完成跨端验收后打 `v1.0.0` 标签。

## 8. 禁止事项

- 不把任何第三方 AI Key 放入官网或 EXE。
- 不把不存在的下载地址当作已上线。
- 不把静态官网改成在线 DWG 翻译器。
- 不把 Cloudflare D1 和本地 SQLite 混用。
- 不在没有真实支付回调的情况下显示购买成功。
- 不在未验证接口前把 APP 切换为 Worker 生产模式。
