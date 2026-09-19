# 桌面截图工具

这些脚本是手动开发工具，不是发布门禁；会切换真实窗口、页面或对话框。运行前保存编辑并停止翻译、导出等操作。不应在无人值守的生产会话中执行。

| 工具 | 输入 | 输出 / 副作用 |
|---|---|---|
| tools/Capture-AllPages.ps1 | ExePath、Pages、Tag、StartupTimeoutSeconds、NoLaunch | 导航页面，调用 Capture-AppWindow 保存截图；打印 PAGE 记录，缺页或截图失败打印 CAPTURE_FAILED 并以退出码 1 结束 |
| tools/Capture-Dialogs.ps1 | ExePath、Tag、NoLaunch | 打开设置/术语库/环境/帮助对话框并尝试取消或关闭；打印 DIALOG 记录，对话框未打开或截图失败打印 DIALOG_FAILED 并以退出码 1 结束 |
| tools/Capture-AppWindow.ps1 | ProcessName、OutDir、Tag | 当前窗口PNG；默认写入系统临时目录 uicap；无窗口退出码 2，无可捕获窗口退出码 3 |
| tools/Capture-DesktopReview.ps1 | Name、OutputDirectory、WindowHandle | 窗口截图，建议显式指定本任务 artifacts 子目录 |

退出码 0 只代表每个页面/对话框都拿到了截图文件；`MISSING_NAV`、打开失败或 `NO_CAPTURE` 都会让脚本以非零退出码结束，便于在批处理中直接判定。

## 启动规则

前两项默认仅在没有 DwgTranslator 实例时启动。若有运行实例，报 APP_ALREADY_RUNNING 并停止，绝不强杀或代替用户处理未保存工作。

`-NoLaunch` 明确复用现有窗口；它不是只读模式，仍会导航/打开对话框。多实例时脚本选择找到的首个有效窗口，因此先确认只保留待检查实例。脚本不能证明任务空闲，不要让它替代正式隔离 UI smoke。

从仓库根目录手动使用：

```powershell
# 已保存工作，确认只有目标窗口时：
powershell -NoProfile -File tools/Capture-AllPages.ps1 -NoLaunch -Tag review
powershell -NoProfile -File tools/Capture-Dialogs.ps1 -NoLaunch -Tag review

# 单张截图可直接指定本轮证据目录：
powershell -NoProfile -File tools/Capture-AppWindow.ps1 -OutDir artifacts/my-review -Tag settings

# 不操作真实窗口的启动保护回归：
powershell -NoProfile -File tests/BuildPipeline/Test-CaptureLaunchSafety.ps1
```

启动保护回归解析真实脚本的启动块，再在模拟 Get-Process / Start-Process 环境执行四种组合（有/无实例 × 默认/NoLaunch），另检查不存在显式终止进程命令，共10项。不等价于实际屏幕截图或所有窗口交互验收。
