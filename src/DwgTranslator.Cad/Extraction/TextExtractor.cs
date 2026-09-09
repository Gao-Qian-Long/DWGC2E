#if GSTARCAD
using Gssoft.Gscad.ApplicationServices;
#else
using Autodesk.AutoCAD.ApplicationServices;
#endif
#if GSTARCAD
using Gssoft.Gscad.DatabaseServices;
#else
using Autodesk.AutoCAD.DatabaseServices;
#endif
using DwgTranslator.Cad;
using DwgTranslator.Core.Models;
#if GSTARCAD
using CadRuntimeException = Gssoft.Gscad.Runtime.Exception;
#else
using CadRuntimeException = Autodesk.AutoCAD.Runtime.Exception;
#endif

namespace DwgTranslator.Cad.Extraction;

/// <summary>
/// Extracts text entities from a DWG file using AutoCAD .NET API.
/// Supports: DBText, MText, AttributeReference, Dimension, MLeader, Table.
/// </summary>
public class TextExtractor
{
    private readonly int _maxNestingDepth;

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
        var visitedBlocks = new HashSet<ObjectId>();

        using var transaction = db.TransactionManager.StartTransaction();
        try
        {
            var modelSpace = (BlockTableRecord)transaction.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            entities.AddRange(ExtractFromBlock(transaction, modelSpace, string.Empty, 0, visitedBlocks));

            var layoutDict = (DBDictionary)transaction.GetObject(
                db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (var entry in layoutDict)
            {
                if (entry.Key == "Model") continue;

                var layout = (Layout)transaction.GetObject(entry.Value, OpenMode.ForRead);
                var btrId = layout.BlockTableRecordId;
                if (btrId.IsValid)
                {
                    var btr = (BlockTableRecord)transaction.GetObject(btrId, OpenMode.ForRead);
                    entities.AddRange(ExtractFromBlock(transaction, btr, string.Empty, 0, visitedBlocks));
                }
            }

            transaction.Commit();
        }
        catch (CadRuntimeException ex)
        {
            Log.Error(ex, "Error extracting text entities");
            transaction.Abort();
            throw;
        }

        // Stamp source file path when available (multi-file writeback safety)
        try
        {
            var sourcePath = db.Filename;
            if (!string.IsNullOrWhiteSpace(sourcePath))
            {
                foreach (var e in entities)
                    e.SourceFilePath = sourcePath;
            }
        }
        catch { }

        Log.Information("Extracted {Count} text entities", entities.Count);
        return entities;
    }

    /// <summary>
    /// Extract text entities from a specific block table record.
    /// </summary>
    private List<TextEntity> ExtractFromBlock(
        Transaction tr, BlockTableRecord btr, string parentBlockName, int depth,
        HashSet<ObjectId> visitedBlocks)
    {
        var entities = new List<TextEntity>();

        if (depth > _maxNestingDepth)
        {
            Log.Warning("Max nesting depth ({Depth}) reached in block {Name}", _maxNestingDepth, btr.Name);
            return entities;
        }

        if (btr.IsAnonymous)
            return entities;

        if (!visitedBlocks.Add(btr.ObjectId))
            return entities;

        foreach (ObjectId objId in btr)
        {
            var dbObject = tr.GetObject(objId, OpenMode.ForRead);
            if (dbObject == null) continue;

            switch (dbObject)
            {
                case AttributeDefinition definition when !definition.Constant:
                    var template = TextEntityFactory.CreateFromDBText(tr, definition, "AttributeDefinition", parentBlockName);
                    template.Status = TranslationStatus.Skipped;
                    entities.Add(template);
                    break;
                case DBText dbText:
                    entities.Add(TextEntityFactory.CreateFromDBText(tr, dbText, "DBText", parentBlockName));
                    break;

                case MText mText:
                    entities.Add(TextEntityFactory.CreateFromMText(tr, mText, "MText", parentBlockName));
                    break;

                case Dimension dim:
                    entities.Add(TextEntityFactory.CreateFromDimension(tr, dim, "Dimension", parentBlockName));
                    break;

                case MLeader mLeader:
                    entities.AddRange(TextEntityFactory.CreateFromMLeader(tr, mLeader, parentBlockName));
                    break;

                case Table table:
                    entities.AddRange(TextEntityFactory.CreateFromTable(table, parentBlockName));
                    break;

                case BlockReference blockRef:
                    var nestedBtr = (BlockTableRecord)tr.GetObject(
                        blockRef.BlockTableRecord, OpenMode.ForRead);

                    bool isXref = nestedBtr.IsFromExternalReference;

                    if (blockRef.AttributeCollection.Count > 0)
                    {
                        foreach (ObjectId attId in blockRef.AttributeCollection)
                        {
                            var attRef = (AttributeReference)tr.GetObject(attId, OpenMode.ForRead);
                            var entity = TextEntityFactory.CreateFromAttribute(tr, attRef, "AttributeReference",
                                string.IsNullOrEmpty(parentBlockName) ? nestedBtr.Name : parentBlockName);
                            entity.IsXref = isXref;
                            entities.Add(entity);
                        }
                    }

                    if (!isXref)
                    {
                        var nestedEntities = ExtractFromBlock(tr, nestedBtr,
                            string.IsNullOrEmpty(parentBlockName) ? nestedBtr.Name : parentBlockName,
                            depth + 1, visitedBlocks);
                        entities.AddRange(nestedEntities);
                    }
                    break;
            }
        }

        return entities;
    }
}
