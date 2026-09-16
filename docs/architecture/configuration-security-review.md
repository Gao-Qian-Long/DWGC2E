# 配置与源码凭据检查（2026-09-16）

## 已核对的边界

- 默认配置：根 `settings.json.example` 的API凭据为空。APP项目只有一条链接到输出settings.json的Content，输入明确是example模板，不是本地settings.json。
- 本地配置：根settings.json/.env/.env.local、APP开发appsettings、Worker .dev.vars/.env.production均通过Git实际忽略规则检查；不存在这些本地凭据文件名已进入Git索引的情况。仅有gitignore不代表已跟踪文件会被自动移除，因此两项分别检查。
- Worker：wrangler.toml无API_KEY、密码/pepper、私钥、访问/刷新令牌或EZFPY_KEY赋值；部署标识、域名和非秘密开关不误判为凭据。没有修改线上开关或Worker Secrets。

## 源码扫描结果

快照包含422个Git已跟踪及未被忽略的候选路径，364个现存UTF-8文本文件已扫描；56个旧路径已删除并有搬迁审查记录，另外2个二进制为集中图标与必需内嵌CAD DLL，未按文本扫描。没有读取被忽略的用户配置、账户数据库或运行目录。

规则覆盖PEM私钥标记、常见提供商密钥、JWT字面量、凭据字段的非空字符串赋值。16处候选全部在测试文件中：内存SQLite测试的虚构账户、邮件模拟传输的测试key、UI受控登录失败用的假密码、回环地址API测试及本轮负向配置检查。已结合测试上下文复核，而非因文件名包含test就忽略。未发现需移除的真实凭据；报告不保存凭据值。

**限制：** 这是当前可入库文本的规则扫描与候选复核，不是“全历史、全部格式、零秘密”的证明。未扫描Git历史、二进制内部、被忽略本地数据，也没有向服务端验证任何候选值。

## 可重复的配置检查

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests/BuildPipeline/Test-SourceConfiguration.ps1
```

14项检查通过，包括嵌套非空凭据负向样例、默认模板来源、忽略规则、已跟踪敏感文件名及Worker秘密赋值。本检查不构建APP、不修改配置、不访问网络；输出只含类型和路径，不包含秘密值。它是显式手动检查入口，尚未冒称已接入自动发布流程。

证据位于 `artifacts/installer-safety-20260916-resumed/source-security/scan.json` 与 `configuration-gate.log`。扫描脚本及候选位置只存该任务证据目录，未知/历史文件不因本次扫描自动删除。