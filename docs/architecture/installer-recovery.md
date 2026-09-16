# 便携安装恢复

## 实现与职责
- Install.ps1：包验证、路径/运行状态检查、安装步骤、快捷方式与清单；异常触发恢复。
- InstallTransaction.ps1：文件写入前快照、备份哈希、持久日志、精确路径恢复、下一次安装处理未完成事务。
- 不引入新生产程序集，不修改CAD回写或业务API。

## 边界
普通异常自动恢复；进程被中止时，下次运行同目录安装前恢复。成功后记录Committed再清理自身备份；恢复失败保留日志，不强杀进程、不递归清理安装目录。变更后的个人配置、损坏/越界日志、链接路径会阻止恢复。磁盘物理故障、掉电导致文件系统/备份同时损坏不承诺可恢复。此机制针对便携安装入口；Inno入口未由此脚本替换。

## 验证
Test-InstallTransaction.ps1：首次安装/升级四阶段故障、进程中断、恢复锁定后重试、日志越界/备份篡改、隔离快捷方式、个人配置保护，共15场景。Test-Installer.ps1：70项；Test-InstallerBootstrap.ps1：13项。均在隔离目录，夹具EXE不会启动。不冒充真实断电或所有Windows环境验证。

## 发布
发布门禁运行事务及中文入口测试。New-ReleasePackage.ps1必须同时携带Install.ps1、InstallTransaction.ps1、Uninstall.ps1，不能手工只复制一个安装脚本。

## 本次交付证据
- 版本：2.1.1+ui.20260916-225348.a3f6f1f
- 安装事务：16 个隔离故障/中断/安全场景通过。
- 便携发布：已更新 `D:\DWGC2E\release`，保留一份上一版本回滚副本。
- 最终压缩包：`D:\DWGC2E\artifacts\installer-safety-20260916-resumed\final-delivery\DwgTranslator-2.1.1-win-x64.zip`
