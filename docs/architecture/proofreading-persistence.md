# 校对保存边界

显式保存的当前工作区快照位于现有账号数据目录的 `proofreading.json`。路径由AccountWorkspace.DirectoryFor(App.AppDataDir, ActiveAccountId)生成；guest与各账号隔离。

- UI仅管理编辑/提示/状态；ProofreadingStore负责校验、读写和恢复匹配。
- 提交成功才清除dirty；失败不能继续离开。错误或未来版本原文件保留。
- 每次保存替换该账号上一份快照；清空工作区同步清空快照。未保存编辑不是自动保存。
- 启动与切回账号重新读取DWG/DXF，核对文件哈希及实体身份后恢复译文，不从JSON恢复写回几何。变化/缺失的图纸跳过并提示。
- 不改变TaskManager翻译检查点，不自动翻译/写回。人工校对仍需显式导出生成图纸。
- 格式Version=1；存储仅进程内串行，不支持多实例编辑合并；不提供加密、云同步或无限历史。记录包含路径和图纸文字，不含凭据。

## 回归

正常交付使用tools/Publish-Desktop.ps1（全门禁包含Core存储单测和UI保存/失败/账号切换/新服务图启动测试）。Core单测只读写隔离临时目录。

独立进程探针：先运行dotnet run --project tests/ProofreadingRestartProbe -c Release -- save <空的隔离证据目录>，进程结束后再运行同一项目的restore <同一目录>。不会使用真实用户图纸或账户；保留生成的验证材料。只证明Core及真实图纸读取器的跨进程恢复，不冒充完整GUI重启。

2026-09-16交付192900的原始证据见artifacts/installer-safety-20260916-resumed/proofreading-persistence/。
