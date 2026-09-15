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

## 继续治理批次 — 2026-09-15 12:38（Asia/Shanghai）

本节更新网页与 APP 候选构建状态，前文作为历史记录保留。Worker / D1 未变更，未再次清空用户。

### 网页已上线
- Git main `1a6880dae9f7b83e183777928236d5536f5241fb`。
- Pages `82675145-f871-488a-9b3f-956e29058a87`，clone/build/deploy 全部 success。
- 线上脚本确认含客服入口焦点返回、反馈重复提交与草稿保护修复。
- 实际只读检查：health/plans=200；匿名 profile/devices=401；新测试脚本公开路径=404。
- 本地 14/14 请求/代理测试；75/75 布局/脚本检查；390/1440px 隔离交互检查通过（FAQ、文字带、灯箱、移动菜单、客服反馈）。未向生产反馈接口提交数据。

### APP 新候选包（尚未公开发行）
- 源码提交 `2ca5ab6`，已推送 pro。
- JsonTaskStore 移除失败后复制覆盖旧文件，保存失败/恢复在进度区提示；当前翻译不因辅助状态写失败中止。
- CAD 纯包络/角度数学抽取并用 10,000 组固定输入与原公式等价比较，不修改实体写入副作用。
- 核心 103/103；APP/GstarCAD 插件构建通过；受控 UI_SMOKE=PASS；RELEASE_PACKAGE/CAD_PLUGIN_PACKAGE=OK。
- 版本 `2.1.1+ui.20260915-123656.2ca5ab6`。
- EXE `D:\DWGC2E\artifacts\publish-20260915-123656\DwgTranslator.exe`。
- ZIP `D:\DWGC2E\artifacts\DwgTranslator-win-x64-GstarCAD-20260915-123656.zip`。
- SHA256(EXE) `482877D0E0A9B8BD21CEB131C6B3885BB414D1F3E5C63CEB28C07D609B5560B8`。
- 未替换 release 目录或官网下载安装包，未完成 AutoCAD 实机验收。

证据保存在受保护基线目录的 app-continuation-publish.txt、pages-continuation-status.json、website-continuation-live.json；本次页面截图在 artifacts/governance-20260915/continuation。

剩余门槛保持有效：真实 CAD 样图与账号切换压力矩阵、完整流水线重构、完整安全负向矩阵、远程隔离恢复/支付演练、公开下载发行和备份清理。进度区保存提示不是持久置顶告警。没有把部分测试通过写成全部治理完成。
