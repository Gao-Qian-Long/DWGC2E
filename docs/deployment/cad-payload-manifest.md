# CAD 私有依赖清单契约

CAD 构建的 WriteCadPlatformManifest 目标在生成 cad-platform.txt 后生成 cad-files.txt。它列出当前插件输出目录中全部非 PDB 文件的相对路径（不列自身）。App 的现有嵌入、构建输出与发布输出复制流程会一起携带这份清单。CAD SDK 引用仍设 Private=False；安装和发布验证拒绝清单中登记宿主SDK程序集。

## 安装、自检、卸载

- 新包：安装和自检读取同一清单，复制/比较全部声明文件，包括子目录中的卫星程序集及清单本身。缺少声明依赖、缺少核心文件、重复/越界/重定向路径或非法清单都会拒绝，不只检查主DLL。
- 旧包：没有cad-files.txt时沿用三文件契约以保持已有浩辰包兼容；不推测源目录中的额外文件都是私有依赖。新包不得故意省略清单规避依赖验证。
- 安装记录：目标插件目录的dwgc2e-installed-files.json记录实际安装文件的SHA256。已有未登记的额外目标文件不自动覆盖；旧版三个主文件仍可按原有修复流程更新。
- 卸载：有记录的文件只有内容匹配安装哈希才删除；修改过的文件、未知文件保留。只要保留了修改文件就保留原始记录，避免第二次卸载误按旧规则删除。没有记录的历史安装继续使用旧三文件卸载规则。
- 升级清单不再需要的旧依赖暂留，并保留原始归属哈希供卸载核对；不在安装中自动清理未知/过期DLL。

## 验证入口

- Core测试：CadPluginPayloadTests（19例，隔离模拟CAD，不加载DLL）。
- tests/CadIntegration/Test-CadPluginInstaller.fsx（既有16项）。
- tests/BuildPipeline/Test-CadPayloadManifest.ps1 -EvidenceDir <新目录>（运行真实清单目标，不运行Build/Restore/Publish）。
- tests/BuildPipeline/Test-CadPayloadVerifier.ps1 -EvidenceDir <新目录>（需已构建Core；隔离启动发布验证器）。

## 不能扩大宣称的范围

这是私有依赖交付一致性修复，不是完整事务式安装。中途复制失败仍可能需要修复；不承诺整组DLL自动回滚。路径检查拒绝已存在的联接，不承诺抵御检查后被其他进程替换路径的竞态。旧版无记录安装不具备新哈希保护。现代AutoCAD宿主、真实升级/重启仍需验收。2026-09-16此修改尚未通过完整APP发布门禁，尚未安装到release。

## 2026-09-16 18:24：统一本地交付已完成

经其他任务冻结确认后，正常Publish-Desktop.ps1 -Clean已通过全部门禁并更新D:/DWGC2E/release：2.1.1+ui.20260916-182234.a3f6f1f。EXE SHA256=7574D0A42B933370549FC1CBFD13DBC0B69513074513B967103B8AE0D94531A4。
Clean/Restore/Build/Publish/包验证通过；安装保护65项、Core314通过/1跳过、布局21项、CAD安装16项、UI受控API冒烟与任务持久化进程中断检查通过。源图标与CAD依赖清单已进入构建；没有修改回写算法或部署网站。
旧release的3个便携文件逐个哈希一致，唯一回滚为artifacts/release-backup-20260916-182234。正常保留策略移除3项旧构建/备份/包，不代表历史未知数据已清理。
迁移核验57项通过，reader读取副本及两处icon映射已记录经审查内容变化；不代表混合工作树全部语义审完。整体目标仍未完成，完整安装事务回滚、历史状态清理和延期真实业务验收仍保留。
证据：artifacts/installer-safety-20260916-resumed/cad-payload-review/{final-publish.log,installed-verification.json,moves-final-reviewed.json}。本段更新上文“源码未发布/三条映射未审”时点状态，历史记录不删除。