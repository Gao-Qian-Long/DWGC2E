# 回写 MText 排版与碰撞检测修复计划

## 概述

修复两个主要 Bug：
1. **多行 MText 单词间隔过大**：回写时文字宽度估算使用了多行拼接宽度（将所有 `\P` 行拼接成一行），导致矩形宽度不收缩，单词被拉伸填充
2. **文字与图层线条干涉**：离线路径无碰撞检测、在线路径碰撞检测遗漏线条实体、位移策略未检查图框边界

---

## 问题诊断

### Bug 1：多行 MText 宽度估算错误

#### 数据流

```
TextExtractor/DwgReaderService 读取
    → TextEntity (MTextRectangleWidth, MTextHasHardBreaks, ...)
    → 翻译服务
    → 回写引擎 (3个路径):
        ├── DwgWriterService (ACadSharp 离线) ✅ 已部分修复
        ├── AcadWriterEngine + LayoutOptimizer (AutoCAD 在线) ❌ 使用拼接宽度
        └── TextReplacer (AutoCAD 在线旧版) ❌ 使用拼接宽度
```

#### 根因

**LayoutOptimizer.OptimizeMText() L48-49:**
```csharp
string textForEstimation = translatedText.Replace("\\P", " ");
double translatedWidth = EstimateTextWidth(textForEstimation, mtext.TextHeight);
```
将所有行拼接为一行估算宽度，导致 `widthRatio` 远大于真实值，宽度收缩逻辑永不触发。

**TextReplacer.ReplaceMText() L198:**
```csharp
double translatedEstWidth = EstimateTextWidth(entity.TranslatedText, mText.TextHeight);
```
同样未按 `\P` 拆分行。

**AcadWriterEngine.RebuildMTextWithLineBreaks() L393-461:**
基于词数均分（`segments.Count / targetLineCount`），不感知字符宽度，各行宽度极不均匀。

#### 正确做法参考

**DwgWriterService.ApplyScaling(CadMText) L447-466** 已正确实现：
```csharp
var logicalLines = SplitMTextLines(translatedText);
double maxLineWidth = 0;
foreach (var line in logicalLines)
{
    double lineW = EstimateTextWidth(line, currentHeight);
    if (lineW > maxLineWidth) maxLineWidth = lineW;
}
// ...
double effectiveWidth = lineCount > 1 ? maxLineWidth : concatenatedWidth;
```

---

### Bug 2：文字与线条干涉

| 根因 | 位置 |
|---|---|
| ACadSharp 离线路径无碰撞检测 | DwgWriterService.cs:599 仅有高度上限 |
| TryDisplaceText 不检查图框边界 | CollisionDetector.cs:379 |
| CollectPotentialColliders 未显式收集 Line/Polyline 等 | CollisionDetector.cs:249 |
| 碰撞 padding 偏小 (25%) | CollisionDetector.cs:137 |

---

## 修复计划

### P1: LayoutOptimizer.OptimizeMText — 按 `\P` 拆分行取最大宽度

**文件**: `src/DwgTranslator.Cad/Replacement/LayoutOptimizer.cs`

**修改内容**:
1. 将 L48-49 的拼接估算替换为按 `\P` 拆分取 `maxLineWidth`
2. 同时计算行数，用于后续高度估算
3. 所有使用 `translatedWidth` 和 `widthRatio` 的逻辑适配

---

### P2: TextReplacer.ReplaceMText — 按 `\P` 拆分行取最大宽度

**文件**: `src/DwgTranslator.Cad/Replacement/TextReplacer.cs`

**修改内容**:
1. L198 替换为按 `\P` 拆分取 `maxLineWidth`
2. L196-221 的矩形宽度调整逻辑适配为按行估算

---

### P3: AcadWriterEngine.RebuildMTextWithLineBreaks — 智能分行

**文件**: `src/DwgTranslator.Cad/Replacement/AcadWriterEngine.cs`

**修改内容**:
1. 用基于 `TextWidthEstimator.EstimateTextWidth` 的贪心分行替换当前词数均分
2. 逐词累加宽度，接近 `总宽度 / 目标行数` 时换行

---

### P4: DwgWriterService.ApplyScaling(CadMText) — 宽度约束加强

**文件**: `src/DwgTranslator.Core/Services/DwgWriterService.cs`

**修改内容**:
1. 在 `lineCount == 1`（无 `\P` 断行）的多行场景下，增加基于 `OriginalWidth` 的保守上限

---

### P5: CollisionDetector.CollectPotentialColliders — 增加线条实体

**文件**: `src/DwgTranslator.Cad/Replacement/CollisionDetector.cs`

**修改内容**:
1. 显式收集 `Line`、`Polyline`、`Arc`、`Circle`、`Spline` 等几何实体
2. 对线条实体使用更大的碰撞 padding（防止文字与线条重叠）

---

### P6: CollisionDetector.TryDisplaceText — 图框边界检查

**文件**: `src/DwgTranslator.Cad/Replacement/CollisionDetector.cs`

**修改内容**:
1. 增加 `Extents3d? frame` 参数
2. 验证位移后位置不超出图框
3. 调用方 `AcadWriterEngine.ReplaceEntity()` 传入 `closestFrame`

---

### P7: DwgWriterService — 离线路径碰撞约束

**文件**: `src/DwgTranslator.Core/Services/DwgWriterService.cs`

**修改内容**:
1. MText 高度缩放下限从 0.5 降到 0.4
2. 单行 Text 宽度缩放下限从 0.6 降到 0.5

---

### P8: CollisionDetector padding 提升

**文件**: `src/DwgTranslator.Cad/Replacement/CollisionDetector.cs`

**修改内容**:
1. L137 `padding = originalHeight * 0.25` → `0.35`

---

## 修改文件清单

| # | 文件 | 修改项 |
|---|---|---|
| P1 | `src/DwgTranslator.Cad/Replacement/LayoutOptimizer.cs` | 按 `\P` 拆分行取最大宽度 |
| P2 | `src/DwgTranslator.Cad/Replacement/TextReplacer.cs` | 按 `\P` 拆分行取最大宽度 |
| P3 | `src/DwgTranslator.Cad/Replacement/AcadWriterEngine.cs` | 智能分行算法；传入 frame |
| P4 | `src/DwgTranslator.Core/Services/DwgWriterService.cs` | 宽度约束加强；碰撞高度约束 |
| P5 | `src/DwgTranslator.Cad/Replacement/CollisionDetector.cs` | 线条实体收集；图框边界检查；padding 提升 |

---

## 执行顺序

1. ✅ 全面审查代码 → 已完成
2. P1: LayoutOptimizer 宽度估算修复（核心修复，影响最大）
3. P2: TextReplacer 宽度估算修复
4. P3: AcadWriterEngine 智能分行
5. P4: DwgWriterService 宽度约束加强
6. P5: CollisionDetector 线条实体收集
7. P8: CollisionDetector padding 提升
8. P6: CollisionDetector 图框边界检查
9. P7: DwgWriterService 离线碰撞约束
