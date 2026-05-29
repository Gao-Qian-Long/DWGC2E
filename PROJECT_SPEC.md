这份文档是专为 AI 编程助手（如 Cursor, Windsurf, GitHub Copilot）设计的**系统级构建规范**。请将此 Markdown 文件保存为 `PROJECT_SPEC.md` 并置于项目根目录，作为 AI 开发时的全局上下文（Rules / System Prompt）。

---

# DWG 图纸中英文互译桌面端程序 - AI 构建规范

## 1. 项目概述与核心原则
本项目是一个基于 AutoCAD .NET API 的桌面端插件/独立程序，用于实现 DWG 图纸的中英文精准互译。
**⚠️ AI 开发最高准则：**
- **准确性 > 自动化**：绝不追求一键全自动。必须实现“提取 → Excel人工校对 → 回写”的三段式工作流。
- **原生 API > 第三方库**：DWG 读写仅限使用 AutoCAD 官方 .NET API，禁止使用 ezdxf/libredwg 等可能导致格式丢失的库。
- **术语硬替换 > AI 自由发挥**：翻译前必须进行术语库确定性匹配，AI 仅处理未命中部分。
- **事务安全**：所有 CAD 数据库操作必须在 `Transaction` 内完成，严禁跨事务持有 `DBObject` 引用。
- **格式码保护**：MTEXT/DIMENSION 中的格式控制符（`\P`, `{...}`）必须在翻译前后完整保留。

## 2. 技术栈锁定
| 组件 | 选型 | 版本/说明 |
| :--- | :--- | :--- |
| 语言 | C# | .NET 8.0 (Class Library) |
| CAD API | AutoCAD .NET API | 2024版 (AcCoreMgd.dll, AcDbMgd.dll, AcMgd.dll) |
| UI 框架 | WPF | .NET 8, MVVM 模式 (CommunityToolkit.Mvvm) |
| 翻译 API | DeepL API | Pro 版 (支持 Glossary) |
| 数据交换 | EPPlus | Excel 读写 (非商业用途可用 MiniExcel 替代) |
| 序列化 | System.Text.Json | 配置文件/中间态存储 |
| 日志 | Serilog | 结构化日志，记录每个实体的处理状态 |
| 测试 | xUnit + Moq | 翻译逻辑单元测试，CAD 逻辑集成测试 |

## 3. 项目结构规范
AI 生成代码时必须严格遵守以下目录结构：
```text
DwgTranslator/
├── src/
│   ├── DwgTranslator.Core/        # 纯业务逻辑，不引用CAD API
│   │   ├── Translation/           # 翻译引擎、术语库、格式码解析器
│   │   ├── Models/                # TextEntity, TranslationPair, GlossaryEntry
│   │   └── Services/              # ITranslationService, IGlossaryService
│   ├── DwgTranslator.Cad/         # CAD API 相关，引用 AcDbMgd 等
│   │   ├── Extraction/            # TextExtractor, EntityVisitor
│   │   ├── Replacement/           # TextReplacer, FontMapper, SizeAdjuster
│   │   └── Commands/              # AutoCAD CommandMethod 入口
│   └── DwgTranslator.App/         # WPF 宿主应用
│       ├── ViewModels/
│       ├── Views/
│       └── Converters/
├── tests/
│   ├── DwgTranslator.Core.Tests/  # 翻译逻辑单元测试（可脱离CAD运行）
│   └── DwgTranslator.Cad.Tests/   # CAD 集成测试（需CAD环境）
├── glossaries/                    # 术语库 JSON/Excel 文件
├── prompts/                       # AI 翻译 Prompt 模板
└── PROJECT_SPEC.md                # 本文件
```

## 4. 核心模块实现细则

### 4.1 文本提取器
- **遍历范围**：ModelSpace + 所有 Layouts + 嵌套 BlockTableRecord（递归深度≤10）。
- **支持实体类型**：`DBText`, `MText`, `AttributeReference`, `Dimension`, `MLeader`, `Table`, `ArcAlignedText`。
- **输出模型**：
    ```csharp
    public class TextEntity
    {
        public string Handle { get; set; }      // 唯一标识，回写定位用
        public string RawText { get; set; }     // 原始文本
        public string PlainText { get; set; }   // 去除格式码后的纯文本（送翻译）
        public string FormatTemplate { get; set; } // 格式码模板（翻译后回填）
        public string EntityType { get; set; }
        public double Height { get; set; }
        public double Rotation { get; set; }
        public string TextStyleName { get; set; }
        public string BlockName { get; set; }   // 所属块名（空=模型空间）
        public bool IsXref { get; set; }        // 是否来自外部参照
        public Point3d Position { get; set; }
    }
    ```
- **⚠️ 关键约束**：
    - XREF 实体默认标记 `IsXref=true`，UI 层默认跳过。
    - 嵌套块内的 AttributeReference 必须记录完整路径：`BlockRefHandle/AttribTag`。
    - MTEXT 格式码解析使用正则：`\{\$$^}]*\}|\$$A-Za-z][^;]*;`，提取后生成带占位符的模板。

### 4.2 翻译引擎
- **三阶段管道**：
    1.  **术语预替换**：遍历术语表，将匹配项替换为 `__GLOSSARY_{index}__` 占位符。
    2.  **DeepL API 调用**：批量请求（≤50条/次），设置 `context="Mechanical engineering CAD drawing"`，`formality="prefer_more"`。
    3.  **占位符还原**：将译文中的占位符替换回术语表对应译文。
- **Prompt 模板**（存于 `prompts/deepl_context.txt`）：
    ```text
    You are a professional mechanical engineering translator.
    Translate the following CAD drawing annotations from {source} to {target}.
    Rules:
    - Keep it concise, suitable for technical drawings.
    - DO NOT translate placeholders like __GLOSSARY_0__.
    - Preserve all formatting codes (\P, {\f...}, etc.) exactly as-is.
    - Do not add explanations or notes.
    ```
- **容错**：API 失败时重试3次（指数退避），仍失败则标记该条目为 `TranslationFailed`，不阻塞整体流程。

### 4.3 Excel 对照编辑器
- **导出格式**：
    | Handle | 原文 | 译文 | 术语命中 | 状态 | 备注 |
    | :--- | :--- | :--- | :--- | :--- | :--- |
    | 1A2B | 轴承座 | Bearing Housing | ✅ | 待确认 | |
    | 3C4D | \P公差{\fSymbol;±}0.05 | \PTolerance {\fSymbol;±}0.05 | ❌ | 已翻译 | 格式码已保留 |
- **导入校验**：回写前验证 Handle 有效性、译文非空、格式码完整性。

### 4.4 安全回写器
- **字体映射规则**：
    - 中文→英文：SimHei/SimSun → Arial/Helvetica
    - 英文→中文：Arial/Helvetica → SimHei（大字体：gbcbig.shx）
- **自适应缩放**：
    ```csharp
    // 伪代码：译文宽度超过原文1.5倍时自动缩小字号
    double newWidth = MeasureTextWidth(translatedText, textStyle, height);
    if (newWidth > originalWidth * 1.5)
    {
        height *= originalWidth / newWidth * 0.95; // 留5%余量
    }
    ```
- **块属性同步**：修改 `AttributeReference` 后，必须调用 `BlockReference.SyncAttributes()` 或手动更新块定义。
- **⚠️ 安全机制**：回写前自动备份原文件至 `.bak`；回写失败时回滚整个 Transaction。

## 5. AI 开发执行协议

### 5.1 分步交付顺序
**严格按以下顺序生成代码，每步完成后必须通过验证再进入下一步：**
1.  **环境搭建**：创建解决方案、配置 NuGet、编写一个 `TESTCMD` 命令验证 CAD API 连通性。
2.  **Core 层 - 格式码解析器**：编写 `FormatCodeParser` + 单元测试（≥20个用例覆盖各种格式码组合）。
3.  **Core 层 - 术语库服务**：实现加载、匹配、占位符替换 + 单元测试。
4.  **Core 层 - 翻译服务**：实现 DeepL 调用 + Mock 测试。
5.  **Cad 层 - 文本提取器**：在 CAD 中测试提取结果，与手动检查对比。
6.  **App 层 - Excel 导出/导入**：验证数据完整性。
7.  **Cad 层 - 回写器**：**先用副本测试**，验证字体、缩放、格式码还原。
8.  **App 层 - WPF 界面**：MVVM 绑定、进度条、错误提示。
9.  **集成测试**：端到端测试完整工作流。

### 5.2 代码质量要求
- 所有公开方法必须有 XML 文档注释。
- CAD 操作必须包裹在 `try-catch` 中，捕获 `Autodesk.AutoCAD.Runtime.Exception`。
- 禁止使用 `async void`（除 CommandMethod 入口外）。
- 翻译相关纯逻辑必须可在无 CAD 环境下单元测试。
- 每次提交代码前，AI 必须自检是否符合本规范第4节的所有约束。

### 5.3 常见陷阱提醒（AI 必读）
| 陷阱 | 正确做法 |
| :--- | :--- |
| 跨事务访问 DBObject | 只存储 ObjectId/Handle，使用时重新 GetObject |
| MTEXT.Contents 直接赋值 | 必须先解析格式码，翻译纯文本，再拼回模板 |
| 修改块属性后未同步 | 调用 SyncAttributes() 或更新 BlockTableRecord |
| 忽略匿名块 | 遍历时检查 BlockTableRecord.IsAnonymous |
| DeepL 超限 | 实现令牌桶限流，Batch ≤50条 |
| 字体不存在 | 回写前检查 TextStyleTable 是否存在目标字体，不存在则降级 |
| 中文乱码 | 确保 BigFont 正确设置，或使用 TrueType 字体 |

## 6. 验收标准
- [ ] 提取器能正确处理嵌套≤5层的块属性和动态块。
- [ ] 格式码解析器单元测试通过率 100%。
- [ ] 术语库命中率 ≥90%（基于测试图纸）。
- [ ] 回写后图纸在 AutoCAD 2020-2025 均可正常打开，无审计错误。
- [ ] 译文溢出图框的比例 ≤5%。
- [ ] 1000条文本提取+翻译+回写全流程 ≤3分钟。
- [ ] 所有 CAD 操作有完整日志，可追溯每个实体的处理结果。

---

