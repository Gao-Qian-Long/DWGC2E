using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using DwgTranslator.Cad;

namespace DwgTranslator.Cad.Replacement;

public static class CollisionResolver
{
    private const double MinHeightScale = 0.35;

    public static bool Resolve(
        Entity entity,
        BlockTableRecord btr,
        Transaction tr,
        Database db,
        double originalHeight,
        out ObjectId? newEntityId)
    {
        newEntityId = null;
        if (originalHeight <= 0) return true;
        if (entity is Dimension or MLeader) return true;

        try
        {
            entity.RecordGraphicsModified(true);
            if (entity is MText mt) mt.RecordGraphicsModified(true);

            Extents3d initialBounds;
            try { initialBounds = CollisionDetector.GetCorrectedBounds(entity); }
            catch { return true; }

            var nearbyEntities = CollisionDetector.CollectNearbyEntities(entity, btr, tr, originalHeight);

            if (!CollisionDetector.HasAnyCollision(entity, nearbyEntities))
            {
                Log.Debug("CollisionResolver: {Handle} no initial collision", entity.Handle);
                return true;
            }

            Log.Information("CollisionResolver: {Handle} has collisions, resolving...", entity.Handle);

            double minHeight = originalHeight * MinHeightScale;
            var savedState = SaveEntityState(entity);

            // Strategy 1: MText wrap (force line wrapping for long single-line text)
            if (entity is MText mtext)
            {
                if (TryWrapMText(mtext, btr, tr, db, originalHeight))
                {
                    Log.Information("CollisionResolver: {Handle} resolved by MText wrap", entity.Handle);
                    return true;
                }
            }

            // Strategy 2: Height scaling (binary search down to 35%)
            entity.RecordGraphicsModified(true);
            if (entity is MText mt2) mt2.RecordGraphicsModified(true);
            try { _ = CollisionDetector.GetCorrectedBounds(entity); } catch { goto restoreAndFail; }

            double currentHeight = GetEntityHeight(entity);
            if (currentHeight > minHeight)
            {
                if (TryScaleHeight(entity, btr, tr, db, currentHeight, minHeight, originalHeight))
                {
                    Log.Information("CollisionResolver: {Handle} resolved by height scaling", entity.Handle);
                    return true;
                }
            }

            // Strategy 3: DBText -> MText conversion (only for DBText, not AttributeReference)
            if (entity is DBText dbText && entity is not AttributeReference)
            {
                if (TryConvertDBTextToMText(dbText, btr, tr, originalHeight, out var convertedMTextId))
                {
                    newEntityId = convertedMTextId;
                    Log.Information("CollisionResolver: {Handle} converted DBText->MText", entity.Handle);
                    return true;
                }
            }

            restoreAndFail:
            Log.Warning("CollisionResolver: {Handle} all strategies exhausted", entity.Handle);
            RestoreEntityState(entity, savedState);
            return false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "CollisionResolver: unexpected error for {Handle}", entity.Handle);
            return false;
        }
    }

    private static double GetEntityHeight(Entity entity) => entity switch
    {
        AttributeReference a => a.Height,
        DBText t => t.Height,
        MText m => m.TextHeight,
        _ => 0
    };

    private static void SetEntityHeight(Entity entity, double height)
    {
        switch (entity)
        {
            case AttributeReference a: a.Height = height; break;
            case DBText t: t.Height = height; break;
            case MText m: m.TextHeight = height; break;
        }
    }

    private static Dictionary<string, object> SaveEntityState(Entity entity)
    {
        var state = new Dictionary<string, object>();
        switch (entity)
        {
            case AttributeReference a:
                state["Position"] = a.Position;
                state["Height"] = a.Height;
                break;
            case DBText t:
                state["Position"] = t.Position;
                state["Height"] = t.Height;
                break;
            case MText m:
                state["Location"] = m.Location;
                state["TextHeight"] = m.TextHeight;
                state["Width"] = m.Width;
                break;
        }
        return state;
    }

    private static void RestoreEntityState(Entity entity, Dictionary<string, object> state)
    {
        try
        {
            switch (entity)
            {
                case AttributeReference a:
                    if (state.TryGetValue("Position", out var apos)) a.Position = (Point3d)apos;
                    if (state.TryGetValue("Height", out var ah)) a.Height = (double)ah;
                    break;
                case DBText t:
                    if (state.TryGetValue("Position", out var pos)) t.Position = (Point3d)pos;
                    if (state.TryGetValue("Height", out var h)) t.Height = (double)h;
                    break;
                case MText m:
                    if (state.TryGetValue("Location", out var loc)) m.Location = (Point3d)loc;
                    if (state.TryGetValue("TextHeight", out var th)) m.TextHeight = (double)th;
                    if (state.TryGetValue("Width", out var w)) m.Width = (double)w;
                    break;
            }
            entity.RecordGraphicsModified(true);
            if (entity is MText mt) mt.RecordGraphicsModified(true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "CollisionResolver: failed to restore entity state");
        }
    }

    private static bool TryWrapMText(
        MText mtext,
        BlockTableRecord btr,
        Transaction tr,
        Database db,
        double originalHeight)
    {
        try
        {
            mtext.RecordGraphicsModified(true);
            mtext.RecordGraphicsModified(true);
            bool isSingleLine = !mtext.Contents.Contains("\\P");
            if (!isSingleLine) return false;

            Extents3d currentBounds;
            try { currentBounds = CollisionDetector.GetCorrectedBounds(mtext); }
            catch { return false; }

            double currentWidth = currentBounds.MaxPoint.X - currentBounds.MinPoint.X;
            double originalWidth = mtext.Width > 0 ? mtext.Width : currentWidth;

            if (currentWidth <= originalWidth * 1.2) return false;

            double wrapWidth = Math.Max(originalWidth * 0.85, currentWidth * 0.5);
            if (wrapWidth < 10.0) wrapWidth = 10.0;

            mtext.Width = wrapWidth;
            mtext.ColumnType = ColumnType.NoColumns;
            mtext.RecordGraphicsModified(true);
            mtext.RecordGraphicsModified(true);

            var nearbyEntities = CollisionDetector.CollectNearbyEntities(mtext, btr, tr, originalHeight);

            try
            {
                if (!CollisionDetector.HasAnyCollision(mtext, nearbyEntities))
                {
                    Log.Information("MText {Handle}: wrapped to width={W:F1} (was {Orig:F1})",
                        mtext.Handle, wrapWidth, currentWidth);
                    return true;
                }
            }
            catch { }

            mtext.Width = 0;
            mtext.RecordGraphicsModified(true);
            mtext.RecordGraphicsModified(true);
            return false;
        }
        catch (Exception ex)
        {
            Log.Debug("CollisionResolver: TryWrapMText failed for {Handle}: {Error}", mtext.Handle, ex.Message);
            return false;
        }
    }

    private static bool TryScaleHeight(
        Entity entity,
        BlockTableRecord btr,
        Transaction tr,
        Database db,
        double currentHeight,
        double minHeight,
        double originalHeight)
    {
        double lo = minHeight;
        double hi = currentHeight;
        double bestHeight = -1;

        for (int i = 0; i < 20; i++)
        {
            if (hi - lo < 0.005) break;
            double mid = (lo + hi) / 2.0;
            SetEntityHeight(entity, mid);
            entity.RecordGraphicsModified(true);
            if (entity is MText mt) mt.RecordGraphicsModified(true);

            try
            {
                var nearbyEntities = CollisionDetector.CollectNearbyEntities(entity, btr, tr, originalHeight);
                if (!CollisionDetector.HasAnyCollision(entity, nearbyEntities))
                {
                    bestHeight = mid;
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }
            catch { hi = mid; }
        }

        if (bestHeight > 0)
        {
            SetEntityHeight(entity, bestHeight);
            entity.RecordGraphicsModified(true);
            if (entity is MText mt) mt.RecordGraphicsModified(true);
            Log.Information("Entity {Handle}: height scaled {Old:F2} -> {New:F2}",
                entity.Handle, currentHeight, bestHeight);
            return true;
        }

        SetEntityHeight(entity, currentHeight);
        entity.RecordGraphicsModified(true);
        if (entity is MText mt2) mt2.RecordGraphicsModified(true);
        return false;
    }

    private static bool TryConvertDBTextToMText(
        DBText dbText,
        BlockTableRecord btr,
        Transaction tr,
        double originalHeight,
        out ObjectId newMTextId)
    {
        newMTextId = ObjectId.Null;
        try
        {
            var mtext = new MText
            {
                Location = dbText.Position,
                TextHeight = dbText.Height,
                TextStyleId = dbText.TextStyleId,
                Rotation = dbText.Rotation,
                Contents = dbText.TextString,
                ColumnType = ColumnType.NoColumns
            };

            Extents3d dbBounds;
            try { dbBounds = dbText.GeometricExtents; }
            catch { mtext.Dispose(); return false; }

            double textWidth = dbBounds.MaxPoint.X - dbBounds.MinPoint.X;
            double wrapWidth = Math.Max(textWidth * 0.7, originalHeight * 8);
            mtext.Width = wrapWidth;

            btr.AppendEntity(mtext);
            tr.AddNewlyCreatedDBObject(mtext, true);

            mtext.RecordGraphicsModified(true);
            mtext.RecordGraphicsModified(true);

            var nearbyEntities = CollisionDetector.CollectNearbyEntities(mtext, btr, tr, originalHeight);

            try
            {
                if (!CollisionDetector.HasAnyCollision(mtext, nearbyEntities))
                {
                    dbText.Erase();
                    newMTextId = mtext.ObjectId;
                    Log.Information("DBText {Handle} -> MText {NewHandle} (width={W:F1})",
                        dbText.Handle, mtext.Handle, wrapWidth);
                    return true;
                }
            }
            catch { }

            mtext.Erase();
            return false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "CollisionResolver: TryConvertDBTextToMText failed for {Handle}", dbText.Handle);
            return false;
        }
    }
}