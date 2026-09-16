# 默认安装目录调整与 CAD 续验（2026-09-16）

## 已完成
- 用户确认最终目录名为 DWGC2E。Inno 与便携 Install.ps1 首次安装默认 D:\DWGC2E，D:\ 不存在/不可访问时回退 C:\DWGC2E。探测只检查目录存在，不保证目标可写；权限不足时不绕过权限或静默改装其他盘。
- Inno 升级沿用已有安装目录，安装向导允许手动选目录；便携脚本显式 -TargetDir 优先，不自动搬迁旧安装。
- 未改程序集身份、AppId 或 AppData 数据路径。
- 本机 D:\DWGC2E 是源码工作区，未在该目录执行正式安装；隔离测试目录与源码分离。
- 最终目录名的便携安装回归 21/21；Inno 隔离验收 40/40；新便携安装包及 CAD 依赖校验通过。
- 未重编 APP、替换 release、终止用户 APP、部署后台或网站。

## 证据
D:\DWGC2E\artifacts\install-default-20260916-082921
- named-portable.log：21 项回归，包含 D 存在/不存在分支。
- named-final/results.json：正式安装包路径和 40 项断言；fresh-install.log 包含 Pascal 默认目录两个分支断言。
- named-package：最终名称对应便携包；named-package-verify.log：完整资源及 CAD 依赖通过。
- 初次 inno 测试在“真实 AppData 未改变”断言失败；当时存在运行中的 APP，但无法确证变化来源。未放宽断言或恢复用户配置，随后复测及最终命名版均通过。历史失败日志保留作诊断证据，不作为可交付版本。

## CAD 续验
- 已有浩辰 CAD 2024（24.1.0.0111）成功启动，空白 Drawing1.dwg 可被可访问性接口识别。发布插件平台 GstarCAD，两项托管 DLL 包校验通过。
- 用户说明此前操作屏幕后再次重试，窗口可激活，但命令区点击仍返回 coordinate input geometry is unavailable；截图接口此前返回 SetIsBorderRequired 不支持此接口。键盘 F2 后未观察到命令窗口切换，未继续盲输命令。
- 未加载插件、改安全/自动加载设置、写回图纸或读取真实账户。此项仍是待验收，不能标记通过。
- 下一步需要可稳定操作的 CAD 命令区，或用户手动协助执行隔离回归；真实 CAD、账号/会员/更新、跨版本升级及安装失败恢复仍未关闭。
## CAD 受限宿主验收更新（2026-09-16）
用户手动执行插件命令后，PING成功；合成测试文字回写6/6、内容6/6；详细审计可用范围6/6、新增文字重叠0。原边界限制测试仍为0/6，不抹去；与利用周边空间策略存在规则差异。单行最小字高3.5→1.4，视觉可读性待验收。未覆盖真实云翻译、APP全链路、自动加载或复杂生产图纸。证据：D:\DWGC2E\artifacts\install-default-20260916-082921\cad-host-20260916-085409\status.md 及 layout\audit-request.txt.report。
