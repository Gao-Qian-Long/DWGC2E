using Autodesk.AutoCAD.DatabaseServices;
using DwgTranslator.Core.Services;

namespace DwgTranslator.Cad.Replacement;

/// <summary>
/// Applies font mapping to AutoCAD text entities (DBText and MText).
/// Creates or finds text styles in the database.
/// </summary>
internal static class AcadFontApplier
{
    /// <summary>
    /// Maps the font for a DBText entity based on its current text style.
    /// </summary>
    public static void MapFont(DBText text, string originalStyleName, bool cnToEn, Transaction tr)
    {
        ObjectId? styleId = ResolveStyleId(text.Database, originalStyleName, cnToEn, tr);
        if (styleId.HasValue)
            text.TextStyleId = styleId.Value;
    }

    /// <summary>
    /// Maps the font for an MText entity based on its current text style.
    /// </summary>
    public static void MapFont(MText mtext, string originalStyleName, bool cnToEn, Transaction tr)
    {
        ObjectId? styleId = ResolveStyleId(mtext.Database, originalStyleName, cnToEn, tr);
        if (styleId.HasValue)
            mtext.TextStyleId = styleId.Value;
    }

    private static ObjectId? ResolveStyleId(Database? db, string originalStyleName, bool cnToEn, Transaction tr)
    {
        try
        {
            string? targetFont = FontMapper.MapFontName(originalStyleName, cnToEn);
            if (string.IsNullOrEmpty(targetFont)) return null;

            if (db == null) return null;

            var textStyleTable = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            ObjectId styleId = ObjectId.Null;

            foreach (ObjectId id in textStyleTable)
            {
                if (!id.IsValid) continue;
                var style = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (string.Equals(style.Name, targetFont, StringComparison.OrdinalIgnoreCase))
                {
                    styleId = id;
                    break;
                }
            }

            if (styleId == ObjectId.Null)
            {
                textStyleTable.UpgradeOpen();
                var newStyle = new TextStyleTableRecord { Name = targetFont };
                bool isShx = targetFont.EndsWith(".shx", StringComparison.OrdinalIgnoreCase);
                if (isShx)
                {
                    newStyle.FileName = targetFont;
                    newStyle.BigFontFileName = string.Empty;
                }
                else
                {
                    newStyle.Font = new Autodesk.AutoCAD.GraphicsInterface.FontDescriptor(
                        targetFont, false, false, 0, 0);
                }
                styleId = textStyleTable.Add(newStyle);
                tr.AddNewlyCreatedDBObject(newStyle, true);
            }

            return styleId == ObjectId.Null ? null : styleId;
        }
        catch (Exception ex)
        {
            DwgTranslator.Cad.Log.Warning(ex, "Font mapping failed for style {Style}", originalStyleName);
            return null;
        }
    }
}
