using ACadSharp;
using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.IO;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Translation;
using Serilog;
using System.Text.RegularExpressions;
using CadTextEntity = ACadSharp.Entities.TextEntity;
using CadMText = ACadSharp.Entities.MText;
using CadDimension = ACadSharp.Entities.Dimension;
using CadMultiLeader = ACadSharp.Entities.MultiLeader;
using CadInsert = ACadSharp.Entities.Insert;
using CadAttributeEntity = ACadSharp.Entities.AttributeEntity;
using CadBlockRecord = ACadSharp.Tables.BlockRecord;
using OurTextEntity = DwgTranslator.Core.Models.TextEntity;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Reads DWG files using ACadSharp library and extracts text entities.
/// Filters out empty-text entities automatically.
/// </summary>
public class DwgReaderService : IDwgReaderService
{
    private static readonly Regex MTextFormatRegex = new(
        @"\\[A-Za-z][^;{}]*;|\\U\+[0-9A-Fa-f]{4}|\\[~%%|{}]|%%[cdpuoCDPUO]|\\[A-Za-z]",
        RegexOptions.Compiled);

    public List<OurTextEntity> ExtractFromFile(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("DWG file not found", filePath);

        Log.Information("Reading DWG file: {Path}", filePath);
        var doc = DwgReader.Read(filePath);
        var rawEntities = new List<OurTextEntity>();

        rawEntities.AddRange(ExtractFromBlock(doc.ModelSpace, string.Empty, 0, 10));
        foreach (var layout in doc.Layouts)
        {
            if (layout.Name == "Model") continue;
            if (layout.AssociatedBlock != null)
                rawEntities.AddRange(ExtractFromBlock(layout.AssociatedBlock, string.Empty, 0, 10));
        }

        // Filter: skip entities with empty PlainText (pure format codes, blanks)
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var entities = new List<OurTextEntity>();
        int skipped = 0;
        foreach (var e in rawEntities)
        {
            if (string.IsNullOrWhiteSpace(e.PlainText))
            {
                // Keep in list but mark as Skipped so user can see it
                e.Status = TranslationStatus.Skipped;
                e.TranslatedText = string.Empty;
                e.Notes = $"Source: {fileName} [空文本]";
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
                Log.Error(ex, "Failed to read DWG file: {Path}", filePath);
                result[filePath] = new List<OurTextEntity>();
            }
        }
        return result;
    }

    private List<OurTextEntity> ExtractFromBlock(CadBlockRecord block, string parentBlockName, int depth, int maxDepth)
    {
        var entities = new List<OurTextEntity>();
        if (depth > maxDepth) return entities;

        foreach (var entity in block.Entities)
        {
            switch (entity)
            {
                case CadTextEntity textEntity when entity is not CadMText:
                    entities.Add(CreateTextEntity(textEntity, "DBText", parentBlockName)); break;
                case CadMText mtext:
                    entities.Add(CreateMTextEntity(mtext, parentBlockName)); break;
                case CadDimension dim:
                    entities.Add(CreateDimensionEntity(dim, parentBlockName)); break;
                case CadMultiLeader mleader:
                    entities.AddRange(CreateMultiLeaderEntities(mleader, parentBlockName)); break;
                case CadInsert insert:
                    var bn = string.IsNullOrEmpty(parentBlockName) ? insert.Block?.Name ?? "" : parentBlockName;
                    foreach (var att in insert.Attributes)
                        if (att is CadAttributeEntity attEntity)
                            entities.Add(CreateAttributeEntity(attEntity, bn));
                    if (insert.Block != null && !IsExternalReference(insert.Block))
                        entities.AddRange(ExtractFromBlock(insert.Block, bn, depth + 1, maxDepth));
                    break;
            }
        }
        return entities;
    }

    private static bool IsExternalReference(CadBlockRecord block) =>
        block.Flags.HasFlag(BlockTypeFlags.XRef) || block.Flags.HasFlag(BlockTypeFlags.XRefOverlay);

    private static OurTextEntity CreateTextEntity(CadTextEntity text, string entityType, string blockName) => new()
    {
        Handle = text.Handle.ToString(),
        RawText = text.Value ?? string.Empty,
        PlainText = StripFormatCodes(text.Value ?? string.Empty),
        FormatTemplate = text.Value ?? string.Empty,
        EntityType = entityType, Height = text.Height, Rotation = text.Rotation,
        TextStyleName = text.Style?.Name ?? "Standard", BlockName = blockName, IsXref = false,
        Position = new Point3d(text.InsertPoint.X, text.InsertPoint.Y, text.InsertPoint.Z),
        OriginalWidth = EstimateTextWidth(text.Value ?? string.Empty, text.Height)
    };

    private static OurTextEntity CreateMTextEntity(CadMText mtext, string blockName) => new()
    {
        Handle = mtext.Handle.ToString(),
        RawText = mtext.Value ?? string.Empty,
        PlainText = mtext.PlainText ?? StripMTextFormatCodes(mtext.Value ?? string.Empty),
        FormatTemplate = mtext.Value ?? string.Empty,
        EntityType = "MText", Height = mtext.Height, Rotation = mtext.Rotation,
        TextStyleName = mtext.Style?.Name ?? "Standard", BlockName = blockName, IsXref = false,
        Position = new Point3d(mtext.InsertPoint.X, mtext.InsertPoint.Y, mtext.InsertPoint.Z),
        OriginalWidth = mtext.RectangleWidth > 0 ? mtext.RectangleWidth : EstimateTextWidth(
            mtext.PlainText ?? StripMTextFormatCodes(mtext.Value ?? string.Empty), mtext.Height)
    };

    private static OurTextEntity CreateDimensionEntity(CadDimension dim, string blockName) => new()
    {
        Handle = dim.Handle.ToString(),
        RawText = dim.Text ?? string.Empty,
        PlainText = StripFormatCodes(dim.Text ?? string.Empty),
        FormatTemplate = dim.Text ?? string.Empty,
        EntityType = "Dimension",
        Height = dim.Style?.TextHeight > 0 ? dim.Style.TextHeight : 2.5,
        Rotation = dim.TextRotation,
        TextStyleName = dim.Style?.Name ?? "Standard", BlockName = blockName, IsXref = false,
        Position = new Point3d(dim.TextMiddlePoint.X, dim.TextMiddlePoint.Y, dim.TextMiddlePoint.Z),
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
            Handle = mleader.Handle.ToString(), RawText = textContent, PlainText = plainText,
            FormatTemplate = textContent, EntityType = "MLeader", Height = textHeight,
            Rotation = ctx.TextRotation, TextStyleName = ctx.TextStyle?.Name ?? "Standard",
            BlockName = blockName, IsXref = false,
            Position = new Point3d(ctx.TextLocation.X, ctx.TextLocation.Y, ctx.TextLocation.Z),
            OriginalWidth = 0
        });
        return entities;
    }

    private static OurTextEntity CreateAttributeEntity(CadAttributeEntity att, string blockName) => new()
    {
        Handle = $"{att.Owner.Handle}/{att.Tag}",
        RawText = att.Value ?? string.Empty,
        PlainText = StripFormatCodes(att.Value ?? string.Empty),
        FormatTemplate = att.Value ?? string.Empty,
        EntityType = "AttributeReference", Height = att.Height, Rotation = att.Rotation,
        TextStyleName = att.Style?.Name ?? "Standard", BlockName = blockName, IsXref = false,
        Position = new Point3d(att.InsertPoint.X, att.InsertPoint.Y, att.InsertPoint.Z),
        OriginalWidth = EstimateTextWidth(att.Value ?? string.Empty, att.Height)
    };

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

    private static double EstimateTextWidth(string text, double height) =>
        (text?.Length ?? 0) * height * 0.6;
}