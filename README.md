# DWG Translator（DWGC2E）

面向机械工程 CAD 图纸的 Windows 桌面翻译工具，可提取并翻译 DWG/DXF 中的文字标注，再将译文回写到图纸。支持离线文件处理，也支持通过宿主专用插件在线回写。

## 直接运行

1. 打开 [`release`](./release/) 目录。
2. 双击 `DwgTranslator.exe`。
3. 首次运行后，在软件设置中填写 DeepSeek API Key。
4. 可一次拖入多张 DWG/DXF：左侧按文件显示，双击图纸进入译文列表并直接编辑。
5. 勾选需要输出的图纸后点击“导出”，只选择一次输出文件夹即可批量生成 `*_translated.dwg/.dxf`，无需再次选择源文件。

> Windows 可能对从网络下载的程序显示安全提示；请确认文件来源为本仓库后再运行。

## 主要功能

- 提取 DBText、MText、尺寸、引线、块属性和表格文字
- 中文与英文互译，支持工程术语表
- 保留常用 MText 格式代码
- 自适应文字缩放与碰撞检测
- 支持离线 DWG/DXF 写回
- 支持浩辰 GstarCAD 2024 或 AutoCAD 2025+ 的宿主专用 `NETLOAD` 插件在线写回
- 翻译缓存、日志查看与失败重试

## 运行目录

```text
release/
├─ DwgTranslator.exe          # Windows 可运行程序
├─ settings.json              # 默认配置模板（不包含私人 API Key）
├─ glossaries/                # 默认术语表
├─ prompts/                   # 翻译提示词
└─ CadPlugin/                 # 当前发布包对应宿主的 NETLOAD 插件及平台清单
```

运行时的个人配置、日志、术语表和导出文件保存在 `%APPDATA%\DwgTranslator\`。私人 API Key 和运行日志不会提交到仓库。

## CAD 插件

1. 确认压缩包名称中的平台与 CAD 宿主一致（不同宿主的插件 DLL 不通用）。
2. 启动对应 CAD，输入 `NETLOAD`。
3. 选择 `release\CadPlugin\DwgTranslator.Cad.dll`。
4. 在桌面程序中使用在线写回，或在 CAD 中调用插件命令。

## 从源码构建

环境要求：Windows、.NET 8 SDK；构建 CAD 插件还需要对应宿主的托管 SDK。当前支持 GstarCAD 2024（.NET Framework 4.8）和 AutoCAD 2025+（.NET 8）。

```powershell
dotnet build DwgTranslator.sln
dotnet watch run --project src/DwgTranslator.App/DwgTranslator.App.csproj
```

发布 Windows x64 单文件版本：

```powershell
.\publish.bat
```

如果 AutoCAD SDK 不在默认位置，请设置 `AUTO_CAD_DIR`，或创建本地且不提交的 `Directory.Build.props.user`。

## 源码结构

```text
src/DwgTranslator.Core/   核心模型、服务与翻译流程
src/DwgTranslator.App/    WPF 桌面界面
src/DwgTranslator.Cad/    AutoCAD 插件
tools/LicenseGenerator/   授权码生成工具
tests/                    手工验证工具与测试样例
```

## 注意事项

- 图纸对象冲突时，程序会记录冲突对象；请检查导出结果和应用内日志。
- 建议保留原始图纸，不要直接覆盖唯一副本。
- `UI布局意见.md`、`功能开发.md`、`建议.md`、`BUG反馈.md`、`CLAUDE.md` 等开发记录仅保留在本地，不随发布程序上传。
