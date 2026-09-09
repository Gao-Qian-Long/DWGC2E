#if GSTARCAD
using Gssoft.Gscad.DatabaseServices;
#else
using Autodesk.AutoCAD.DatabaseServices;
#endif
#if GSTARCAD
using Gssoft.Gscad.Geometry;
#else
using Autodesk.AutoCAD.Geometry;
#endif
using DwgTranslator.Core.Models;
using System.Text.RegularExpressions;
#if GSTARCAD
using Point3dCad = Gssoft.Gscad.Geometry.Point3d;
#else
using Point3dCad = Autodesk.AutoCAD.Geometry.Point3d;
#endif

namespace DwgTranslator.Cad.Extraction;

/// <summary>
/// Factory methods for creating TextEntity instances from various AutoCAD entity types.
/// </summary>
internal static class TextEntityFactory
{
    public static TextEntity CreateFromDBText(Transaction tr, DBText dbText, string entityType, string blockName)
    {
        var rawText = dbText.TextString ?? string.Empty;
        var plainText = StripFormatCodes(rawText);

        return new TextEntity
        {
            Handle = dbText.Handle.ToString(),
            RawText = rawText,
            PlainText = plainText,
            FormatTemplate = rawText,
            EntityType = entityType,
            Height = dbText.Height,
            Rotation = dbText.Rotation,
            TextStyleName = GetTextStyleName(tr, dbText.TextStyleId),
            BlockName = blockName,
            IsXref = false,
            Position = new Core.Models.Point3d(dbText.Position.X, dbText.Position.Y, dbText.Position.Z),
            OriginalWidth = EstimateTextWidth(dbText),
            OriginalHeight = dbText.Height
        };
    }

    public static TextEntity CreateFromMText(Transaction tr, MText mText, string entityType, string blockName)
    {
        var rawText = mText.Contents ?? string.Empty;
        var plainText = StripMTextFormatCodes(rawText);
        int lineCount = CountMTextHardLines(rawText);
        bool hasHardBreaks = rawText.Contains("\\P");

        return new TextEntity
        {
            Handle = mText.Handle.ToString(),
            RawText = rawText,
            PlainText = plainText,
            FormatTemplate = rawText,
            EntityType = entityType,
            Height = mText.TextHeight,
            Rotation = mText.Rotation,
            TextStyleName = GetTextStyleName(tr, mText.TextStyleId),
            BlockName = blockName,
            IsXref = false,
            Position = new Core.Models.Point3d(mText.Location.X, mText.Location.Y, mText.Location.Z),
            OriginalWidth = mText.ActualWidth,
            OriginalHeight = mText.TextHeight,
            MTextRectangleWidth = mText.Width,
            MTextLineSpacing = mText.LineSpacingFactor,
            MTextLineSpacingStyle = (int)mText.LineSpacingStyle,
            MTextLineCount = lineCount,
            MTextHasHardBreaks = hasHardBreaks
        };
    }

    public static TextEntity CreateFromDimension(Transaction tr, Dimension dim, string entityType, string blockName)
    {
        var rawText = dim.DimensionText ?? string.Empty;
        var plainText = StripFormatCodes(rawText);

        return new TextEntity
        {
            Handle = dim.Handle.ToString(),
            RawText = rawText,
            PlainText = plainText,
            FormatTemplate = rawText,
            EntityType = entityType,
            Height = 2.5, // Dimension text height is controlled by dimension style (DIMTXT), not per-entity
            Rotation = dim.TextRotation,
            TextStyleName = GetTextStyleName(tr, dim.TextStyleId),
            BlockName = blockName,
            IsXref = false,
            Position = new Core.Models.Point3d(dim.TextPosition.X, dim.TextPosition.Y, dim.TextPosition.Z),
            OriginalWidth = 0,
            OriginalHeight = 2.5
        };
    }

    public static TextEntity CreateFromAttribute(Transaction tr, AttributeReference attRef, string entityType, string blockName)
    {
        var rawText = attRef.TextString ?? string.Empty;
        var plainText = StripFormatCodes(rawText);

        return new TextEntity
        {
            Handle = $"{attRef.OwnerId.Handle}/{attRef.Tag}",
            Status = AttributeTranslationPolicy.IsMetadataTag(attRef.Tag) ? TranslationStatus.Skipped : TranslationStatus.Pending,
            RawText = rawText,
            PlainText = plainText,
            FormatTemplate = rawText,
            EntityType = entityType,
            Height = attRef.Height,
            Rotation = attRef.Rotation,
            TextStyleName = GetTextStyleName(tr, attRef.TextStyleId),
            BlockName = blockName,
            IsXref = false,
            Position = new Core.Models.Point3d(attRef.Position.X, attRef.Position.Y, attRef.Position.Z),
            OriginalWidth = EstimateTextWidth(attRef),
            OriginalHeight = attRef.Height
        };
    }

    public static List<TextEntity> CreateFromMLeader(Transaction tr, MLeader mLeader, string blockName)
    {
        var entities = new List<TextEntity>();

        var textContent = mLeader.MText?.Contents;
        if (!string.IsNullOrEmpty(textContent))
        {
            var plainText = StripMTextFormatCodes(textContent);
            var mtext = mLeader.MText;
            if (mtext == null) return entities;

            entities.Add(new TextEntity
            {
                Handle = mLeader.Handle.ToString(),
                RawText = textContent,
                PlainText = plainText,
                FormatTemplate = textContent,
                EntityType = "MLeader",
                Height = mtext.TextHeight,
                Rotation = mtext.Rotation,
                TextStyleName = GetTextStyleName(tr, mtext.TextStyleId),
                BlockName = blockName,
                IsXref = false,
                Position = new Core.Models.Point3d(mtext.Location.X, mtext.Location.Y, mtext.Location.Z),
                OriginalWidth = mtext.ActualWidth,
                OriginalHeight = mtext.TextHeight
            });
        }

        return entities;
    }

    public static List<TextEntity> CreateFromTable(Table table, string blockName)
    {
        var entities = new List<TextEntity>();

        for (int row = 0; row < table.Rows.Count; row++)
        {
            for (int col = 0; col < table.Columns.Count; col++)
            {
                var cell = table.Cells[row, col];
                // Preserve cells with multiple independent fields/formulas unchanged.
                if (cell.Contents == null || cell.Contents.Count != 1) continue;
                var cellText = GetCellText(cell);
                if (string.IsNullOrWhiteSpace(cellText)) continue;

                entities.Add(new TextEntity
                {
                    Handle = $"{table.Handle}:{row}:{col}",
                    RawText = cellText,
                    PlainText = StripFormatCodes(cellText),
                    FormatTemplate = cellText,
                    EntityType = "Table",
                    Height = (double)(cell.TextHeight ?? 2.5),
                    Rotation = 0,
                    TextStyleName = "Standard",
                    BlockName = blockName,
                    IsXref = false,
                    Position = new Core.Models.Point3d(0, 0, 0),
                    OriginalWidth = 0,
                    OriginalHeight = (double)(cell.TextHeight ?? 2.5)
                });
            }
        }

        return entities;
    }

    private static readonly Regex MTextFormatRegex = new(
        @"(?:\{[^}]*\}|\\[A-Za-z][0-9]*;?|\\[~%%|{}]|\\U\+[0-9A-Fa-f]{4}|%%[a-zA-Z])",
        RegexOptions.Compiled);

    private static string GetCellText(Cell cell)
    {
        try
        {
            var contents = cell.Contents;
            if (contents == null || contents.Count == 0)
                return string.Empty;

            var value = cell.Value;
            if (value != null)
            {
                var text = value.ToString();
                if (!string.IsNullOrEmpty(text))
                    return text;
            }

            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetTextStyleName(Transaction tr, ObjectId styleId)
    {
        if (!styleId.IsValid) return "Standard";
        try
        {
            var style = tr.GetObject(styleId, OpenMode.ForRead) as TextStyleTableRecord;
            return style?.Name ?? "Standard";
        }
        catch
        {
            return "Standard";
        }
    }

    public static string StripFormatCodes(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return MTextFormatRegex.Replace(text, string.Empty).Trim();
    }

    public static string StripMTextFormatCodes(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var result = MTextFormatRegex.Replace(text, string.Empty);
        result = Regex.Replace(result, @"\s{2,}", " ").Trim();
        return result;
    }

    private static double EstimateTextWidth(DBText dbText)
    {
        var text = StripFormatCodes(dbText.TextString ?? string.Empty);
        return Core.Services.TextWidthEstimator.EstimateTextWidth(text, dbText.Height);
    }

    private static double EstimateTextWidth(AttributeReference attRef)
    {
        var text = StripFormatCodes(attRef.TextString ?? string.Empty);
        return Core.Services.TextWidthEstimator.EstimateTextWidth(text, attRef.Height);
    }

    private static int CountMTextHardLines(string rawText)
    {
        if (string.IsNullOrEmpty(rawText)) return 1;

        int count = 1;
        int index = 0;
        while ((index = rawText.IndexOf("\\P", index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += 2;
        }
        return count;
    }
}
