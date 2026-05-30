using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using DwgTranslator.Core.Models;
using Serilog;
using System.Text.RegularExpressions;
using Point3dCad = Autodesk.AutoCAD.Geometry.Point3d;

namespace DwgTranslator.Cad.Extraction;

/// <summary>
/// Extracts text entities from a DWG file using AutoCAD .NET API.
/// Supports: DBText, MText, AttributeReference, Dimension, MLeader, Table.
/// </summary>
public class TextExtractor
{
    private readonly int _maxNestingDepth;
    private static readonly Regex MTextFormatRegex = new(
        @"(?:\{[^}]*\}|\\[A-Za-z][0-9]*;?|\\[~%%|{}])",
        RegexOptions.Compiled);

    public TextExtractor(int maxNestingDepth = 10)
    {
        _maxNestingDepth = maxNestingDepth;
    }

    /// <summary>
    /// Extract all text entities from the current database.
    /// </summary>
    public List<TextEntity> ExtractAll(Database db)
    {
        var entities = new List<TextEntity>();

        using var transaction = db.TransactionManager.StartTransaction();
        try
        {
            // Extract from ModelSpace
            var modelSpace = (BlockTableRecord)transaction.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            entities.AddRange(ExtractFromBlock(transaction, modelSpace, string.Empty, 0));

            // Extract from all Layouts (PaperSpace)
            var layoutDict = (DBDictionary)transaction.GetObject(
                db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (var entry in layoutDict)
            {
                if (entry.Key == "Model") continue; // Skip Model layout

                var layout = (Layout)transaction.GetObject(entry.Value, OpenMode.ForRead);
                var btrId = layout.BlockTableRecordId;
                if (btrId.IsValid)
                {
                    var btr = (BlockTableRecord)transaction.GetObject(btrId, OpenMode.ForRead);
                    entities.AddRange(ExtractFromBlock(transaction, btr, string.Empty, 0));
                }
            }

            transaction.Commit();
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            Log.Error(ex, "Error extracting text entities");
            transaction.Abort();
            throw;
        }

        Log.Information("Extracted {Count} text entities", entities.Count);
        return entities;
    }

    /// <summary>
    /// Extract text entities from a specific block table record.
    /// </summary>
    private List<TextEntity> ExtractFromBlock(
        Transaction tr, BlockTableRecord btr, string parentBlockName, int depth)
    {
        var entities = new List<TextEntity>();

        if (depth > _maxNestingDepth)
        {
            Log.Warning("Max nesting depth ({Depth}) reached in block {Name}", _maxNestingDepth, btr.Name);
            return entities;
        }

        // Skip anonymous blocks
        if (btr.IsAnonymous)
            return entities;

        foreach (ObjectId objId in btr)
        {
            var dbObject = tr.GetObject(objId, OpenMode.ForRead);
            if (dbObject == null) continue;

            switch (dbObject)
            {
                case DBText dbText:
                    entities.Add(CreateTextEntity(dbText, "DBText", parentBlockName));
                    break;

                case MText mText:
                    entities.Add(CreateTextEntity(mText, "MText", parentBlockName));
                    break;

                case Dimension dim:
                    entities.Add(CreateTextEntity(dim, "Dimension", parentBlockName));
                    break;

                case MLeader mLeader:
                    entities.AddRange(ExtractFromMLeader(mLeader, parentBlockName));
                    break;

                case Table table:
                    entities.AddRange(ExtractFromTable(table, parentBlockName));
                    break;

                case BlockReference blockRef:
                    // Recurse into block references
                    var nestedBtr = (BlockTableRecord)tr.GetObject(
                        blockRef.BlockTableRecord, OpenMode.ForRead);

                    // Check if XREF
                    bool isXref = nestedBtr.IsFromExternalReference;

                    // Extract attributes from block reference
                    if (blockRef.AttributeCollection.Count > 0)
                    {
                        foreach (ObjectId attId in blockRef.AttributeCollection)
                        {
                            var attRef = (AttributeReference)tr.GetObject(attId, OpenMode.ForRead);
                            var entity = CreateTextEntity(attRef, "AttributeReference",
                                string.IsNullOrEmpty(parentBlockName) ? nestedBtr.Name : parentBlockName);
                            entity.IsXref = isXref;
                            entities.Add(entity);
                        }
                    }

                    // Recurse into nested block
                    if (!isXref) // Don't recurse into XREFs
                    {
                        var nestedEntities = ExtractFromBlock(tr, nestedBtr,
                            string.IsNullOrEmpty(parentBlockName) ? nestedBtr.Name : parentBlockName,
                            depth + 1);
                        entities.AddRange(nestedEntities);
                    }
                    break;
            }
        }

        return entities;
    }

    private TextEntity CreateTextEntity(DBText dbText, string entityType, string blockName)
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
            TextStyleName = GetTextStyleName(dbText.TextStyleId),
            BlockName = blockName,
            IsXref = false,
            Position = new DwgTranslator.Core.Models.Point3d(dbText.Position.X, dbText.Position.Y, dbText.Position.Z),
            OriginalWidth = EstimateTextWidth(dbText)
        };
    }

    private TextEntity CreateTextEntity(MText mText, string entityType, string blockName)
    {
        var rawText = mText.Contents ?? string.Empty;
        var plainText = StripMTextFormatCodes(rawText);

        return new TextEntity
        {
            Handle = mText.Handle.ToString(),
            RawText = rawText,
            PlainText = plainText,
            FormatTemplate = rawText,
            EntityType = entityType,
            Height = mText.TextHeight,
            Rotation = mText.Rotation,
            TextStyleName = GetTextStyleName(mText.TextStyleId),
            BlockName = blockName,
            IsXref = false,
            Position = new DwgTranslator.Core.Models.Point3d(mText.Location.X, mText.Location.Y, mText.Location.Z),
            OriginalWidth = mText.ActualWidth
        };
    }

    private TextEntity CreateTextEntity(Dimension dim, string entityType, string blockName)
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
            Height = 2.5, // Default dimension text height
            Rotation = 0,
            TextStyleName = GetTextStyleName(dim.TextStyleId),
            BlockName = blockName,
            IsXref = false,
            Position = new DwgTranslator.Core.Models.Point3d(dim.TextPosition.X, dim.TextPosition.Y, dim.TextPosition.Z),
            OriginalWidth = 0
        };
    }

    private TextEntity CreateTextEntity(AttributeReference attRef, string entityType, string blockName)
    {
        var rawText = attRef.TextString ?? string.Empty;
        var plainText = StripFormatCodes(rawText);

        return new TextEntity
        {
            Handle = $"{attRef.OwnerId.Handle}/{attRef.Tag}",
            RawText = rawText,
            PlainText = plainText,
            FormatTemplate = rawText,
            EntityType = entityType,
            Height = attRef.Height,
            Rotation = attRef.Rotation,
            TextStyleName = GetTextStyleName(attRef.TextStyleId),
            BlockName = blockName,
            IsXref = false,
            Position = new DwgTranslator.Core.Models.Point3d(attRef.Position.X, attRef.Position.Y, attRef.Position.Z),
            OriginalWidth = EstimateTextWidth(attRef)
        };
    }

    private List<TextEntity> ExtractFromMLeader(MLeader mLeader, string blockName)
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
                TextStyleName = GetTextStyleName(mtext.TextStyleId),
                BlockName = blockName,
                IsXref = false,
                Position = new DwgTranslator.Core.Models.Point3d(mtext.Location.X, mtext.Location.Y, mtext.Location.Z),
                OriginalWidth = mtext.ActualWidth
            });
        }

        return entities;
    }

    private List<TextEntity> ExtractFromTable(Table table, string blockName)
    {
        var entities = new List<TextEntity>();

        for (int row = 0; row < table.Rows.Count; row++)
        {
            for (int col = 0; col < table.Columns.Count; col++)
            {
                var cell = table.Cells[row, col];
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
                    Position = new DwgTranslator.Core.Models.Point3d(0, 0, 0), // Table cells don't have independent position
                    OriginalWidth = 0
                });
            }
        }

        return entities;
    }

    private static string GetCellText(Cell cell)
    {
        try
        {
            var contents = cell.Contents;
            if (contents == null || contents.Count == 0)
                return string.Empty;

            // Get text value from the cell's Value property
            var value = cell.Value;
            if (value != null)
            {
                var text = value.ToString();
                if (!string.IsNullOrEmpty(text))
                    return text;
            }

            // Fallback: try to get text from TextString property
            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetTextStyleName(ObjectId styleId)
    {
        if (!styleId.IsValid) return "Standard";
        // The TextStyleTableRecord name will be resolved when used in context
        return styleId.Handle.ToString();
    }

    private static string StripFormatCodes(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return MTextFormatRegex.Replace(text, string.Empty).Trim();
    }

    private static string StripMTextFormatCodes(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        // Remove MTEXT-specific format codes
        var result = MTextFormatRegex.Replace(text, string.Empty);
        // Clean up extra whitespace
        result = Regex.Replace(result, @"\s{2,}", " ").Trim();
        return result;
    }

    private static double EstimateTextWidth(DBText dbText)
    {
        // Rough estimation based on character count and height
        var text = dbText.TextString ?? string.Empty;
        return text.Length * dbText.Height * 0.6;
    }

    private static double EstimateTextWidth(AttributeReference attRef)
    {
        var text = attRef.TextString ?? string.Empty;
        return text.Length * attRef.Height * 0.6;
    }
}
