# 安装安全自动测试

以下测试不启动桌面APP、不请求真实支付，不替代人工业务验收。请从项目根目录执行 Windows PowerShell 5.1，每次使用新的 artifacts 结果子目录，保留失败日志后排查，不覆盖已有证据。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests/BuildPipeline/Test-Installer.ps1 -ResultDir artifacts/installer-check/portable
powershell -NoProfile -ExecutionPolicy Bypass -File tests/BuildPipeline/Test-PortableShortcuts.ps1 -ResultDir artifacts/installer-check/create-links
powershell -NoProfile -ExecutionPolicy Bypass -File tests/BuildPipeline/Test-PortableShortcutUninstall.ps1 -ResultDir artifacts/installer-check/remove-links
powershell -NoProfile -ExecutionPolicy Bypass -File tests/BuildPipeline/Test-InstallerPathSafety.ps1 -NodeExe <本机node.exe路径> -ResultDir artifacts/installer-check/path-safety
powershell -NoProfile -ExecutionPolicy Bypass -File tests/BuildPipeline/Test-InstallerBootstrap.ps1 -ResultDir artifacts/installer-check/bootstrap
powershell -NoProfile -ExecutionPolicy Bypass -File tests/BuildPipeline/Test-InnoInstaller.ps1 -PublishDir <已验证的publish目录> -ResultDir artifacts/installer-check/inno
```

| 测试 | 覆盖范围 | 2026-09-16实测 |
|---|---|---|
| Test-Installer | 合成安装包安装/重装/卸载、用户数据保留、源码标记拒绝、清单安全及旧版兼容 | 65项通过 |
| Test-PortableShortcuts | 原始生产创建函数 + 真实COM，所有链接在隔离目录 | 10项通过 |
| Test-PortableShortcutUninstall | 原始生产卸载脚本 + 真实子进程及COM，子进程APPDATA隔离 | 30项通过 |
| Test-InstallerPathSafety | 实际同目标存活进程拒绝、自然退出后安装恢复、目标/包根/卸载路径联接拒绝 | 18项通过 |
| Test-InnoInstaller | 正常安装器仅编译；隔离安装器安装/同版本修复/卸载 | 45项通过（含源码目标拒绝及盘根判定） |
| Test-InstallerBootstrap | 实际中文CMD、中文/空格路径、参数传递、完整提示、无解析错误、失败退出码及目标不变 | 13项通过 |

端到端卸载测试只登记 StartMenu 链接，不改变 Desktop 已知目录，不操作真实用户链接。结果JSON保存被测源码哈希；源码有变化必须重新运行。Inno测试需要本机已安装Inno Setup及简体中文语言包。

未覆盖：便携安装任意中途故障的原子回滚、已重定向/同步桌面等其他机器环境、联接目录竞态、实际账户/CAD/支付业务。绝不能将这些未覆盖项报告为通过。

本次证据统一保存在 `artifacts/installer-safety-20260916-resumed`。这些测试均保留隔离夹具供检查，不自行递归删除；后续清理必须先审阅范围并遵守根AGENTS.md。
路径安全测试复制指定Node可执行文件作为短时测试进程，不运行真实APP，等待进程自然退出。创建的两条目录联接均指向同一新夹具内目录，结果JSON记录Path和Target；保留联接用于检查，后续清理不得递归跟随它们。



中文入口证据：`chinese-bootstrap-final/results.json`。使用生产打包函数转换编码，合成负载不执行；不是资源管理器双击及真实控制台视觉验收。最新安装脚本同时重新通过65项便携安装回归。修复后的本地ZIP及逐文件哈希检查位于 `bootstrap-final-package`，12个归档文件与打包目录一致，APP版本固定为 `2.1.1+ui.20260916-124619.a3f6f1f`；没有上传网站。
## 真实Desktop卸载分支（显式选择，不加入默认自动测试）

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests/BuildPipeline/Test-DesktopShortcutUninstall.ps1 -AllowDesktopFixture -ResultDir artifacts/installer-check/desktop
```

此测试短时使用真实Desktop已知目录。只有当前不存在 `DWG Translator.lnk` 才允许开始；使用不覆盖复制放入本次创建的测试链接，绝不替换现有链接或修改桌面位置/注册表。清理只删除本次创建且哈希未变的单个链接。如果文件被外部修改则保留并报错，不强删。没有显式开关时拒绝执行，已实测该保护。

本机8场景、27项检查通过：正常归属删除、已改内容保留、其他目标保留、自定义参数保留、未登记保留、重复记录拒绝、文件占用拒绝、预览不修改；设置始终保留。真实生产Uninstall.ps1通过独立进程运行，最终Desktop恢复为没有同名快捷方式的初始状态。证据 `desktop-shortcuts-final/results.json`、8份日志。不是APP启动或资源管理器点击验收，也不证明所有重定向/同步桌面环境。