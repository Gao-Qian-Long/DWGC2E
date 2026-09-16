# 动态依赖、资源与运行路径复核

日期：2026-09-16。对应已安装版本：2.1.1+ui.20260916-195051.a3f6f1f。
这是治理要求9/11/19/20/26的增量证据，不是全部动态行为完成证明。本轮只新增独立审计工具、故障夹具与文档；没有重建APP、改业务源码或删除DLL。

## 编译产物验证

- tests/ArchitectureAudit 通过PE元数据读取程序集引用、类型引用与内嵌资源；不通过Assembly.Load执行待审程序集。
- 36项检查通过：桌面Core、已安装CAD Core、已安装CAD插件无直接WPF/WinForms/App程序集依赖和所检查的UI类型引用；21个XAML的编译BAML条目存在；图标仍为WPF内嵌资源。
- APP构建DLL中的4个CAD资源（插件、Core、平台标记、文件清单）与已安装CadPlugin对应文件逐字节SHA256一致；资源集合与cad-files.txt一致。
- 已安装只读默认词库与assets源一致；EXE哈希与build-info一致。便携用户词库不要求等于默认资源，不能据此覆盖用户数据。
- 工程评估32项通过：Core双目标无UI项目/框架直接引用、无测试目录源文件混入、集中资源复制契约存在。
- 故障夹具1个基线通过，3个故意损坏场景均被拒绝：插件资源篡改、清单不一致、新增但未编译的XAML。夹具EXE是明确不可运行的文本占位，仅用于审计器哈希分支，不冒充产品启动测试。

证据：artifacts/installer-safety-20260916-resumed/dependency-audit/。

## 必须保留的动态入口

|用途|入口与加载方式|保留理由/边界|
|---|---|---|
|APP派发CAD|App/CadIntegration/AutoCadInteropService：COM ProgID创建或CAD批处理，NETLOAD后发送DwgTranslateWrite|没有静态using也会被调用；插件与私有Core不能删除|
|CAD命令发现|Cad/Commands中的CommandClass、CommandMethod，Initializer实现IExtensionApplication|宿主动态发现；源码调用次数不能证明死代码|
|插件依赖冲突处理|Initializer.OnAssemblyResolve查询已加载程序集，先完整名再简单名|未新增磁盘加载路径；兼容版本选择仍须实际宿主验证|
|内嵌插件后备|MainViewModel.Environment枚举DwgTranslator.App.Embedded.CadPlugin.*，释放至AppDataDir/CadPlugin|已检查构建DLL资源与安装文件一致，未用本轮审计实际触发释放|
|WPF资源|XAML编译为BAML、icon.ico内嵌、Core本地化ResourceManager|BAML存在不等于所有绑定/视觉效果正确；Core本地化访问器返回键的兜底可能掩盖缺失，需原UI验证配合|
|系统COM|oleaut32.dll与AutoCAD/GstarCAD ProgID|操作系统/CAD宿主提供，不是应复制进src的第三方DLL|
|宿主SDK|CAD工程Ac*/Gc*引用Private=False|由CAD安装提供，不能为补依赖随包复制；本轮只验证当前GstarCAD产物，不声称AutoCAD各版本通过|

## 运行数据与配置路径

- APP默认%AppData%/DwgTranslator；DWGC2E_DATA_DIR为明确覆盖入口。保留历史目录名避免迁移丢数据。
- 首次启动只读程序目录settings.json作为默认来源，正式设置、日志、设备标识及账号数据写入AppDataDir；账号任务/校对/词库/输出按AccountWorkspace隔离。
- 默认词库从程序目录assets/default-glossaries读取；提示词按用户数据prompts、程序目录prompts、内置文本顺序读取。
- CAD派发临时会话位于系统Temp/DwgTranslator/<session>；旧插件参数仍兼容Temp/DwgTranslator/writeback_config.json，不要求源码保留临时JSON。
- CAD Initializer的日志仍使用%AppData%/DwgTranslator/logs，不随APP的DWGC2E_DATA_DIR覆盖。这是当前代码的真实差异，不宣称隔离APP就能隔离真实CAD日志；未为治理强改CAD代码。
- HealthCheck/VerifyLayout/LayoutPlot/LayoutFootprint命令含诊断输出，属于宿主动态入口，先KEEP/REVIEW，不能因名称含TEST或无C#直接调用而删。实际输出由Temp或显式输入路径决定。

## 审计时发现、随后修复的路径问题

**插件来源优先级不一致**：环境安装页面CadPluginDirectory优先已配置插件目录，而实际翻译ResolveCadPluginPath优先程序随附CadPlugin。旧配置仍指向可用旧安装目录时，环境安装与翻译可能选到不同来源。审计时尚未修复；随后201231交付已将已存在文件的选择规则统一，8项Core测试与UI调用方验证通过。无可用文件时仍保留不同后备路径。详见plugin-source/report.md；不是仅凭资源哈希认定修复。

## 重跑与限制

先按正常Publish-Desktop流程得到构建与release，单独运行审计不会构建APP：

    dotnet run --project tests/ArchitectureAudit/ArchitectureAudit.csproj -c Release -- D:\DWGC2E D:\DWGC2E\artifacts\<task>\compiled-resources.json
    powershell -NoProfile -File tests/BuildPipeline/Test-ArchitectureAudit.ps1 -OutputDir artifacts/<task>/negative-cases

报告和夹具目录必须是新路径。审计未验证全部传递NuGet包、反射可达性、任意自定义插件、所有CAD版本，未从压缩单文件EXE解包证明独立构建DLL身份。测试项目不加入主Solution。2026-09-16 后续已将该审计接入 Publish-Desktop：在候选包 build-info 写入后、打包和替换 release 前检查；第三参数显式指定候选目录，不误用旧release。默认两参数入口仍审计release。扩展夹具共8项（3通过、5预期拒绝），另验证候选路径选择及EXE哈希损坏。正式发布仍须完整回归，不因本门禁替代UI或真实CAD验收。
