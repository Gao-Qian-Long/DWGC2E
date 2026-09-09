using ACadSharp;
using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.IO;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Resources;
using DwgTranslator.Core.Translation;
using Serilog;
using System.Text.RegularExpressions;
using CadTextEntity = ACadSharp.Entities.TextEntity;
using CadMText = ACadSharp.Entities.MText;
using CadDimension = ACadSharp.Entities.Dimension;
using CadMultiLeader = ACadSharp.Entities.MultiLeader;
using CadInsert = ACadSharp.Entities.Insert;
using CadAttributeEntity = ACadSharp.Entities.AttributeEntity;
using CadTable = ACadSharp.Entities.TableEntity;
using CadBlockRecord = ACadSharp.Tables.BlockRecord;
using OurTextEntity = DwgTranslator.Core.Models.TextEntity;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Reads DWG and DXF files using ACadSharp library and extracts text entities.
/// Filters out empty-text entities automatically.
/// </summary>
public class DwgReaderService : IDwgReaderService, IDxfReaderService
{
    private static readonly Regex MTextFormatRegex = new(
        @"\\[AaCcFfHhQqSsTtWw][^;{}]*;|\\U\+[0-9A-Fa-f]{4}|\\[PpLlOoKkXx~\\{}]|%%[cdpuoCDPUO]",
        RegexOptions.Compiled);

    public List<OurTextEntity> ExtractFromFile(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("CAD file not found", filePath);

        Log.Information("Reading CAD file: {Path}", filePath);

        // Detect file format and use appropriate reader
        CadDocument doc;
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        if (extension == ".dxf")
        {
            bool isBinary = DxfReader.IsBinary(filePath);
            Log.Information("DXF format: {Type}", isBinary ? "Binary" : "ASCII");
            using var reader = new DxfReader(filePath, OnCadNotification);
            doc = reader.Read();
        }
        else
        {
            doc = DwgReader.Read(filePath);
        }
        var rawEntities = new List<OurTextEntity>();
        // Block definitions are shared by all inserts. Extract each definition once;
        // insert attributes remain instance-specific and are still visited per insert.
        var visitedBlocks = new HashSet<ulong>();

        rawEntities.AddRange(ExtractFromBlock(doc.ModelSpace, string.Empty, 0, 10, visitedBlocks));
        foreach (var layout in doc.Layouts)
        {
            if (layout.Name == "Model") continue;
            if (layout.AssociatedBlock != null)
                rawEntities.AddRange(ExtractFromBlock(layout.AssociatedBlock, string.Empty, 0, 10, visitedBlocks));
        }

        // Filter: skip entities with empty PlainText (pure format codes, blanks)
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var fullPath = Path.GetFullPath(filePath);
        var entities = new List<OurTextEntity>();
        int skipped = 0;
        foreach (var e in rawEntities)
        {
            e.SourceFilePath = fullPath;
            if (string.IsNullOrWhiteSpace(e.PlainText))
            {
                // Keep in list but mark as Skipped so user can see it
                e.Status = TranslationStatus.Skipped;
                e.TranslatedText = string.Empty;
                e.Notes = $"Source: {fileName} {Strings.Get("EmptyText")}";
                entities.Add(e);
                skipped++;
                continue;
            }
            e.Notes = $"Source: {fileName}";
            entities.Add(e);
        }

        if (skipped > 0) Log.Information("Marked {Count} empty/skipped entities from {File}", skipped, fileName);
        Log.Information("Extracted {Count} text entities from {File}", entities.Count, fileName);
        return entities;
    }

    public Dictionary<string, List<OurTextEntity>> ExtractFromFiles(IEnumerable<string> filePaths)
    {
        var result = new Dictionary<string, List<OurTextEntity>>();
        foreach (var filePath in filePaths)
        {
            try { result[filePath] = ExtractFromFile(filePath); }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to read CAD file: {Path}", filePath);
                result[filePath] = new List<OurTextEntity>();
            }
        }
        return result;
    }

    private List<OurTextEntity> ExtractFromBlock(CadBlockRecord block, string parentBlockName, int depth, int maxDepth,
        HashSet<ulong> visitedBlocks)
    {
        var entities = new List<OurTextEntity>();
        if (depth > maxDepth) return entities;
        if (!visitedBlocks.Add(block.Handle)) return entities;

        foreach (var entity in block.Entities)
        {
            switch (entity)
            {
                case ACadSharp.Entities.AttributeDefinition definition when ((int)definition.Flags & 2) == 0:
                    // Non-constant ATTDEF is a default for future inserts, not
                    // displayed text. Existing INSERT attributes are extracted separately.
                    var template = CreateTextEntity(definition, "AttributeDefinition", parentBlockName);
                    template.Status = TranslationStatus.Skipped;
                    entities.Add(template);
                    break;
                case CadTextEntity textEntity when entity is not CadMText:
                    entities.Add(CreateTextEntity(textEntity, "DBText", parentBlockName)); break;
                case CadMText mtext:
                    entities.Add(CreateMTextEntity(mtext, parentBlockName)); break;
                case CadDimension dim:
                    entities.Add(CreateDimensionEntity(dim, parentBlockName)); break;
                case CadMultiLeader mleader:
                    entities.AddRange(CreateMultiLeaderEntities(mleader, parentBlockName)); break;
                case CadTable table:
                    entities.AddRange(CreateTableEntities(table, parentBlockName)); break;
                case CadInsert insert:
                    var bn = string.IsNullOrEmpty(parentBlockName) ? insert.Block?.Name ?? "" : parentBlockName;
                    foreach (var att in insert.Attributes)
                        if (att is CadAttributeEntity attEntity)
                            entities.Add(CreateAttributeEntity(attEntity, bn));
                    if (insert.Block != null && !IsExternalReference(insert.Block))
                        entities.AddRange(ExtractFromBlock(insert.Block, bn, depth + 1, maxDepth, visitedBlocks));
                    break;
            }
        }
        return entities;
    }

    private static bool IsExternalReference(CadBlockRecord block) =>
        block.Flags.HasFlag(BlockTypeFlags.XRef) || block.Flags.HasFlag(BlockTypeFlags.XRefOverlay);

    // All Handle.ToString() calls use "X" format (uppercase hex) to match
    // AutoCAD's native Handle.ToString() output so that entity lookups work.

    private static OurTextEntity CreateTextEntity(CadTextEntity text, string entityType, string blockName) => new()
    {
        Handle = text.Handle.ToString("X"),
        RawText = text.Value ?? string.Empty,
        PlainText = StripFormatCodes(text.Value ?? string.Empty),
        FormatTemplate = text.Value ?? string.Empty,
        EntityType = entityType, Height = text.Height, Rotation = text.Rotation,
        TextStyleName = text.Style?.Name ?? "Standard", BlockName = blockName, IsXref = false,
        Position = new Point3d(text.InsertPoint.X, text.InsertPoint.Y, text.InsertPoint.Z),
        OriginalWidth = EstimateTextWidth(text.Value ?? string.Empty, text.Height),
        OriginalHeight = text.Height
    };

    private static OurTextEntity CreateMTextEntity(CadMText mtext, string blockName)
    {
        string rawValue = mtext.Value ?? string.Empty;
        string plainText = !string.IsNullOrWhiteSpace(mtext.PlainText)
            ? mtext.PlainText
            : StripMTextFormatCodes(rawValue);
        int lineCount = CountMTextHardLines(rawValue);

        return new OurTextEntity
        {
            Handle = mtext.Handle.ToString("X"),
            RawText = rawValue,
            PlainText = plainText,
            FormatTemplate = rawValue,
            EntityType = "MText",
            Height = mtext.Height,
            Rotation = mtext.Rotation,
            TextStyleName = mtext.Style?.Name ?? "Standard",
            BlockName = blockName,
            IsXref = false,
            Position = new Point3d(mtext.InsertPoint.X, mtext.InsertPoint.Y, mtext.InsertPoint.Z),
            OriginalWidth = mtext.RectangleWidth > 0 ? mtext.RectangleWidth : EstimateTextWidth(plainText, mtext.Height),
            OriginalHeight = mtext.Height,
            MTextRectangleWidth = mtext.RectangleWidth,
            MTextLineSpacing = mtext.LineSpacing,
            MTextLineSpacingStyle = (int)mtext.LineSpacingStyle,
            MTextLineCount = lineCount,
            MTextHasHardBreaks = lineCount > 1
        };
    }

    private static OurTextEntity CreateDimensionEntity(CadDimension dim, string blockName) => new()
    {
        Handle = dim.Handle.ToString("X"),
        RawText = dim.Text ?? string.Empty,
        PlainText = StripFormatCodes(dim.Text ?? string.Empty),
        FormatTemplate = dim.Text ?? string.Empty,
        EntityType = "Dimension",
        Height = dim.Style?.TextHeight > 0 ? dim.Style.TextHeight : 2.5,
        Rotation = dim.TextRotation,
        TextStyleName = dim.Style?.Name ?? "Standard", BlockName = blockName, IsXref = false,
        Position = new Point3d(dim.TextMiddlePoint.X, dim.TextMiddlePoint.Y, dim.TextMiddlePoint.Z),
        OriginalHeight = dim.Style?.TextHeight > 0 ? dim.Style.TextHeight : 2.5,
        OriginalWidth = 0
    };

    private static List<OurTextEntity> CreateMultiLeaderEntities(CadMultiLeader mleader, string blockName)
    {
        var entities = new List<OurTextEntity>();
        var ctx = mleader.ContextData;
        if (ctx == null || !ctx.HasTextContents) return entities;
        var textContent = ctx.TextLabel;
        if (string.IsNullOrEmpty(textContent)) return entities;

        var plainText = StripMTextFormatCodes(textContent);
        var textHeight = ctx.TextHeight > 0 ? ctx.TextHeight : 2.5;
        entities.Add(new OurTextEntity
        {
            Handle = mleader.Handle.ToString("X"), RawText = textContent, PlainText = plainText,
            FormatTemplate = textContent, EntityType = "MLeader", Height = textHeight,
            Rotation = ctx.TextRotation, TextStyleName = ctx.TextStyle?.Name ?? "Standard",
            BlockName = blockName, IsXref = false,
            Position = new Point3d(ctx.TextLocation.X, ctx.TextLocation.Y, ctx.TextLocation.Z),
            OriginalHeight = textHeight,
            OriginalWidth = 0
        });
        return entities;
    }

    private static OurTextEntity CreateAttributeEntity(CadAttributeEntity att, string blockName) => new()
    {
        Handle = att.Owner != null ? $"{att.Owner.Handle:X}/{att.Tag}" : $"{att.Handle:X}/{att.Tag}",
        RawText = att.Value ?? string.Empty,
        PlainText = StripFormatCodes(att.Value ?? string.Empty),
        FormatTemplate = att.Value ?? string.Empty,
        EntityType = "AttributeReference", Height = att.Height, Rotation = att.Rotation,
        Status = AttributeTranslationPolicy.IsMetadataTag(att.Tag) ? TranslationStatus.Skipped : TranslationStatus.Pending,
        TextStyleName = att.Style?.Name ?? "Standard", BlockName = blockName, IsXref = false,
        Position = new Point3d(att.InsertPoint.X, att.InsertPoint.Y, att.InsertPoint.Z),
        OriginalHeight = att.Height,
        OriginalWidth = EstimateTextWidth(att.Value ?? string.Empty, att.Height)
    };


    private static List<OurTextEntity> CreateTableEntities(CadTable table, string blockName)
    {
        var entities = new List<OurTextEntity>();
        try
        {
            for (int row = 0; row < table.Rows.Count; row++)
            {
                var rowObj = table.Rows[row];
                if (rowObj?.Cells == null) continue;
                for (int col = 0; col < rowObj.Cells.Count; col++)
                {
                    var cell = rowObj.Cells[col];
                    // Multi-content cells may contain independent fields/formulas/styles.
                    // Treat them as opaque until content-level round-trip support exists.
                    if (cell.HasMultipleContent) continue;
                    var cellText = GetTableCellText(cell);
                    if (string.IsNullOrWhiteSpace(cellText)) continue;

                    double height = 2.5;
                    try
                    {
                        if (cell.Geometry != null && cell.Geometry.ContentHeight > 0)
                            height = cell.Geometry.ContentHeight;
                    }
                    catch { }

                    entities.Add(new OurTextEntity
                    {
                        Handle = $"{table.Handle:X}:{row}:{col}",
                        RawText = cellText,
                        PlainText = StripFormatCodes(cellText),
                        FormatTemplate = cellText,
                        EntityType = "Table",
                        Height = height,
                        Rotation = 0,
                        TextStyleName = "Standard",
                        BlockName = blockName,
                        IsXref = false,
                        Position = new Point3d(0, 0, 0),
                        OriginalWidth = 0,
                        OriginalHeight = height
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to extract table cells from handle {Handle}", table.Handle);
        }
        return entities;
    }

    private static string GetTableCellText(CadTable.Cell cell)
    {
        try
        {
            if (cell.HasMultipleContent && cell.Contents != null && cell.Contents.Count > 0)
            {
                var parts = new List<string>();
                foreach (var content in cell.Contents)
                {
                    var text = content?.CadValue?.ToString();
                    if (!string.IsNullOrWhiteSpace(text)) parts.Add(text!);
                }
                if (parts.Count > 0) return string.Join("\\P", parts);
            }

            var value = cell.Content?.CadValue;
            if (value != null)
            {
                if (!string.IsNullOrWhiteSpace(value.FormattedValue)) return value.FormattedValue;
                if (value.Value != null)
                {
                    var text = value.Value.ToString();
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
                var asString = value.ToString();
                if (!string.IsNullOrWhiteSpace(asString) && asString != value.GetType().FullName)
                    return asString;
            }
        }
        catch { }
        return string.Empty;
    }
    private static string StripFormatCodes(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var result = MTextFormatRegex.Replace(text, string.Empty);
        result = result.Replace("{", "").Replace("}", "");
        return result.Trim();
    }

    private static string StripMTextFormatCodes(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var result = MTextFormatRegex.Replace(text, string.Empty);
        result = result.Replace("{", "").Replace("}", "");
        return Regex.Replace(result, @"\s{2,}", " ").Trim();
    }

    private static double EstimateTextWidth(string text, double height)
        => TextWidthEstimator.EstimateTextWidth(text, height);

    private static int EstimateLineCount(string text, double height, double rectWidth)
        => TextWidthEstimator.EstimateLineCount(text, height, rectWidth);

    /// <summary>
    /// Counts hard line breaks (\P) in an MText raw value to determine original line count.
    /// </summary>
    private static int CountMTextHardLines(string rawValue)
    {
        if (string.IsNullOrEmpty(rawValue)) return 1;
        // \P is the AutoCAD MText paragraph break
        int count = rawValue.Split("\\P", StringSplitOptions.None).Length;
        return Math.Max(1, count);
    }

    /// <summary>
    /// Handles ACadSharp notification events during CAD file parsing.
    /// Captures warnings and errors from the DXF/DWG reader for structured logging.
    /// </summary>
    private static void OnCadNotification(object sender, ACadSharp.IO.NotificationEventArgs e)
    {
        if (e.Exception != null)
            Log.Warning(e.Exception, "ACadSharp [{Type}]: {Message}", e.NotificationType, e.Message);
        else
            Log.Debug("ACadSharp [{Type}]: {Message}", e.NotificationType, e.Message);
    }
}
