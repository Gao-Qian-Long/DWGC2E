# 桌面交付整理与自审结果

记录时间按本机 Asia/Shanghai：2026-09-17。仅桌面本地交付与获准的 Git pro 推送；没有上传网盘/官网、部署 Worker、开启购买或真实付款。

## 当前可运行与用户包

- 版本：2.1.1+ui.20260917-021219.2446a54
- 本机：release/DwgTranslator.exe
- APP SHA256：F56F426AD98346BA864C05483A64A3FE0BCB05CD8DE654A4AE23F05589E56DE9
- 安装器：DWGC2E-2.1.1-win-x64-GstarCAD-20260917-021219-Setup.exe
- Setup SHA256：78EE0221C251E226850F70405E90F53946B13C58DE5BD48BF60125A33E92C810
- Setup 大小：70804779 字节。
- 上传目录：D:/DWGC2E/artifacts/delivery-20260917-021219/installer/public
- 目录严格三文件：Setup.exe、开始使用.txt、SHA256SUMS.txt。
- 唯一上一安装版：D:/DWGC2E/artifacts/release-backup-20260917-021219（015456构建）。
- 权威追溯：artifacts/current-release.json；安装验收：D:/DWGC2E/artifacts/delivery-20260917-021219/installer/acceptance/results.json。

## 已实施

1. 单个生产 Setup 作为主交付，自动释放运行组件，不再要求用户执行安装.cmd 或编辑 JSON；保留内部便携兼容测试。
2. 运行时白名单、候选哈希、构建前后源文件快照、生产/隔离安装器区分、已安装 EXE 哈希与原子最新清单。
3. 全新安装、真实跨构建升级、修复、卸载、所有载荷文件哈希和个人数据保留验收接入发布门禁。
4. 便携数据保护从固定目录扩展为全部未知文件；旧插件按清单认定所有权，assets/CadPlugin 中未知个人文件不再整体丢弃。
5. 日常 start.bat 只启动；publish.bat 完整发版；dev.bat 保持发布后启动的兼容行为。维护手册和工具目录已更新。
6. 八份桌面旧方案/旧交付/旧清单归档；运行翻译提示词保留。旧手工插件副本从 Git 移除、磁盘保留并忽略，不进入安装器。
7. 受控清理保留最新候选/包和一个回滚；旧 delivery 只在所有文件与所有权清单一致时删除。未知数据或未完成交付保留为例外。

## 自审发现与迭代

- 修正子 PowerShell 输出混入安装器 receipt 的风险，日志与结构化返回分流。
- 修正运行时-only系统 dotnet 遮蔽用户 .NET SDK 的情况；构建机自动寻找 SDK，不让最终用户配置开发环境。
- 不再从个人 release 编译升级测试包，改用匹配版本的干净候选，避免任何安装器携带个人配置。
- 二次发布暴露 PowerShell 5.1 把 File.Replace 的空备份参数转换为非法路径：020526发布因此自动恢复015456 release。已用明确备份路径修复，加入替换/文件占用真实回归，021219再次完整发布成功。
- 补齐“新装当前候选”测试，避免把仅有前版升级误称全新安装。
- 最后源文件核对跨 PowerShell 版本按路径+哈希集合比较：348项一致；不同版本的排序差异不当成源码变动。

## 验证结果

- Core：383通过、1跳过、0失败。
- 安装保护：70项；安装事务/中断：16场景；CMD引导：13项。
- 新交付回归：24项；运行载荷隐私负向测试：7项；限量保留/个人数据/WhatIf/篡改交付目录测试通过。
- 布局、任务中断恢复、CAD插件部署16/16、UI smoke、图标、候选架构/资源审计通过。
- Inno安装验收：90项，新装、015456→021219升级、修复及卸载均通过。
- 独立最终核对：APP和Setup哈希匹配；设置、默认便携提示词和词库对上一安装版哈希不变；一个候选、一个回滚、public仅三文件。
- UI smoke 使用受控API，不是真实支付/生产翻译验收；Inno使用隔离目标，不是干净VM或所有CAD版本认证。

本地证据：artifacts/release-governance-20260917/final-verification.json、publish-verified.log、failed-publish-rollback.json、cleanup-regression.log、staged-security.json。

## 范围、剩余项与保留例外

初始盘点18,279文件；本轮最终盘点20,292文件、67个显式构建引用。增长主要来自本轮安装/负向回归与失败证据，不能把“建立清理机制”说成“全部历史文件已清空”。盘点是元数据/路径与关键实现审查，并非逐字审读每个文件。

批量删除受到执行策略拒绝：64个旧测试APP/Setup约4.39GiB、根空配置/误路径缓存/旧bin和obj未删；失败交付证据保留。完整清单见 cleanup-exceptions-20260917.md，逐文件可删清单在本任务artifacts。没有改用其他执行途径绕过拒绝。

cf-worker的50条既有未提交/未跟踪路径保持原状；没有混入桌面提交或部署。已提交的桌面工作包含前序UI/可靠性/目录迁移改动及其测试，不宣称299文件都是本轮新写或纯搬迁。

签名、干净Windows+目标CAD真实图纸端到端验收、实际网盘上传仍未执行。当前包是GstarCAD平台；用户仍需已有匹配CAD、网络和有效账户/额度。不能承诺未安装CAD或任意CAD版本都能直接导出DWG。

日常执行请以 desktop-release-management.md 和 current-release.json 为准；历史文件的“最新”只对应原日期。
