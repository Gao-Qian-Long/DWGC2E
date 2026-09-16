# 清理例外与未执行项（2026-09-17）

这不是“全部删除完成”证明。受控发布的旧候选/ZIP/上一版回滚使用现有 Clean-DesktopArtifacts.ps1 管理；以下内容单独审查，未混入用户 public 安装包。

## 已整理

- 旧桌面 UI/重构方案与旧最终交付说明移到 docs/history/desktop-20260916；现行入口是 desktop-release-management.md。
- 运行时 deepl_context.txt 保留；它用于实际翻译，不是开发聊天记录。
- 手工复制的 src/DwgTranslator.App/Embedded/CadPlugin 旧 DLL/平台文件不被当前项目引用，已从版本控制移除并加入精确忽略。磁盘副本未删；真实内嵌插件来自本次 CAD 构建输出，由资源/哈希/UI 测试验证。
- 正式 Setup 只含运行时白名单；PDB、build-info、架构报告、源码、AGENTS、测试和日志不进安装器。

## 执行环境拒绝的清理

本任务的批量文件删除调用被执行策略拒绝，未改用其他工具绕过。以下均仍在磁盘上：

1. 64 个历史生成 APP/Setup，共 4,717,407,489 字节（约 4.39 GiB）。它们位于已审查的历史测试目录，不是当前 candidate、release、rollback 或 public。逐文件路径/版本/大小/SHA256见本地 artifacts/release-governance-20260917/reviewed-binary-cleanup.csv；计划仅删列出的 EXE，不递归删除这些目录，日志/数据库/个人数据/截图都保留。
2. 根目录 settings.json（0 字节）、字面量 %SystemDrive% 目录中的四个缓存文件（合计961,024字节）、迁移后 tools/LayoutRegression 下的 bin/obj。已经确认路径和用途，但批量删除调用未执行成功。真正的系统盘缓存、APPDATA配置不在清理范围内。
3. 本任务失败交付目录及失败候选仍保留，供失败/回滚追溯；不作为“最新版”上传。尤其 020526 候选虽通过安装验证，但清单替换失败后已回滚，不能根据其 installer-manifest 单独认定已安装。

不要为了清掉几个目录运行 git clean -fdx、全盘删除 *.db 或递归删除所有 artifacts。历史目录中存在数据库恢复备份、与其他任务有关的证据及测试联接；这些需要按实际所有权处理。

## 有意长期保留

- artifacts/保留的数据备份 以及所有未确认可丢弃的数据库恢复资料。
- 其他任务的独有截图、后台/云端审计证据；不当作 APP 构建垃圾。
- cf-worker 全部既有未提交改动；本次不覆盖、不混入桌面代码提交、不部署。

当前交付是否可上传，只看 artifacts/current-release.json 及对应 public 三文件；清理例外不进入安装器，也不要求最终用户处理。
