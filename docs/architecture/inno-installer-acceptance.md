# Inno 安装器隔离验收

日期：2026-09-16。仅调整安装编译与测试，不修改桌面业务，不重新构建 APP，不替换 release，不部署后台或网站。

## 结果

- Inno Setup 6.7.3：正式分支、隔离分支编译成功，最终编译日志无 Warning/Error。
- `tests/BuildPipeline/Test-InnoInstaller.ps1`：40/40 通过。
- 便携安装链 `tests/BuildPipeline/Test-Installer.ps1`：19/19 通过。
- 输入版本：`2.1.1+ui.20260916-080934.a3f6f1f`。
- 正式安装包只编译，未在用户系统执行；实际安装、同版本覆盖修复及卸载使用同一脚本的隔离测试分支。

## 验证范围

1. 首次安装的 EXE、CAD DLL、默认配置、提示词、术语资源哈希一致。
2. 测试分支拒绝改写安装目标，用户数据与五个快捷方式均重定向到独立 fixture，不启动 APP、不注册正式卸载项。
3. 同版本覆盖安装修复人为损坏的测试插件，保留九个测试用户文件。
4. 卸载删除已安装的程序/插件/内置资源以及测试快捷方式，保留九个用户文件。目录因用户文件而保留属于预期，不应强制删除。
5. 真实 AppData 中 settings.json 和 glossaries、正式卸载注册项及 release EXE 哈希未变化。未对正在写入的真实日志或所有运行数据作此保证。

## 可重复执行

在仓库根目录运行；PublishDir 必须指向有 build-info.json 的干净发布候选，不要使用包含个人数据的 release 作为打包源。

```powershell
powershell -NoProfile -File tests/BuildPipeline/Test-InnoInstaller.ps1 `
  -PublishDir "D:\DWGC2E\artifacts\publish-20260916-080934"
```

默认编译器：`%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`，可用 `-Compiler` 指定。需要 ChineseSimplified.isl。可选 `-ResultDir` 必须位于仓库 artifacts 下；默认使用唯一测试目录。测试前校验候选 EXE 与 build-info.json 哈希。

`installer/DwgTranslator.iss` 仅在显式定义 TestRoot 时重定向目录、禁用 APP 启动及正式卸载注册；常规编译保留正式路径和应用身份。**isolated-build 下的安装包仅供测试，禁止分发。** production-build 是正式分支输出，但当前未签名，不能把工具安装文件的有效签名理解成产品安装包已经签名。

## 证据

最终证据：`D:\DWGC2E\artifacts\inno-acceptance-20260916-082013\final`。

- results.json：40 项断言与安装包路径。
- production-build.log、isolated-build.log：编译结果。
- reject-override.log、fresh-install.log、repair-install.log、uninstall.log：安装器执行日志。
- 上一级 portable-regression.log：便携安装链 19 项回归。

## 尚未关闭

- 跨版本升级、运行中/锁定目标的 Inno 行为、安装中断与事务回滚没有因本轮测试而得到验证。
- 便携卸载仍为手动安全提示，没有恢复危险的自动删除或全局终止进程。
- 真实 CAD 宿主翻译回写、真实登录/会员/支付/更新仍需独立验收，不进行真实付款。
- 历史未知文件保留待确认；本轮未删除源码、提交 Git 或改动其他任务的内容。