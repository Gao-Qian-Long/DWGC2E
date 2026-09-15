# 已验证发布结果 — 2026-09-15 11:32（Asia/Shanghai）

## 当前生产

- Worker: `318abce7-a28d-480f-9692-51c7f7ca8fe2`。
- D1: `dwg-translator-prod-v2` / `6e28247a-4de7-499a-9a94-176347e1a8c7`。
- Pages: `982a439d-9b49-4f4c-8891-439dffbe3a6c`，Git main `01d832763c06330416886d33fe6f5e1514b9a78e`。Cloudflare clone/build/deploy 各阶段均 success；不是仅上传后假定成功。
- 官方域名已返回新管理页脚本；health/plans=200，匿名 profile/devices/admin-feedback=401。
- 内部 tests/docs/tools 路径=404。
- 两仓库当前变更已独立提交推送（APP/Worker pro；网站 main）；没有重置旧 HEAD、丢弃未提交成果或公开私有仓库。

## 本地候选 APP

- `D:\DWGC2E\artifacts\publish-20260915-112737\DwgTranslator.exe`
- `D:\DWGC2E\artifacts\DwgTranslator-win-x64-GstarCAD-20260915-112737.zip`
- 版本 `2.1.1+ui.20260915-112737.038e4f5`
- EXE SHA256 `CF4F1E2AF7F69FB4293AC7B2E34772DA5E07911CEE2D89C306EA01F1670457A9`
- 93 核心测试通过；受控 UI_SMOKE=PASS；发布依赖/插件打包校验通过。
- GstarCAD 环境构建，未进行 AutoCAD 实机图纸等价验证；未替换官网公开下载，也未覆盖用户正在使用的 release。

## 验证与交付边界

Worker 130/130 + 类型检查；网站 14/14 + 75 次布局/脚本检查；线上 6 次只读浏览器检查。不存在“全六阶段均已完成”的结论。

当前旧用户数据不在新生产库中，旧账号无法继续登录，应重新注册。旧 D1 及受保护导出保留作恢复档案，不等于已经永久删除全部历史数据。

旧备份递归清理操作在执行前被工具策略拒绝；未改用其他工具绕过。已移除经过引用核查的 portal.js 旧支付处理代码，但本地大量备份/缓存清理仍未完成。未清理 .wrangler/release/业务文件。

仍需完成：全路由安全负向矩阵、可信来源限流设计、隔离远程恢复/支付演练、APP 完整流水线/CAD 重构及实机验证、公开发行渠道和旧备份清理。详见治理台账。

受保护恢复与详细操作日志：`D:\DWGC2E_Baselines\20260915-102235`；该目录不得上传或公开。
