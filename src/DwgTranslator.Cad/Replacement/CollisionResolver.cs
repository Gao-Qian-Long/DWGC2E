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
using DwgTranslator.Cad;
using WBC = DwgTranslator.Core.Models.WritebackConstants;

namespace DwgTranslator.Cad.Replacement;

/// <summary>
/// Online collision resolution using AutoCAD GeometricExtents + IntersectWith.
/// Strategy order:
///   1. Keep original height
///   2. MText wrap / width constraint
///   3. Micro-nudge position
///   4. Height binary-search scale (last resort)
///   5. Optional DBText -> MText conversion then re-run wrap
/// Retests after every change.
/// </summary>
public static class CollisionResolver
{
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
        if (entity is Dimension or MLeader or Table) return true;

        try
        {
            Mark(entity);

            // If extents cannot be computed, skip rather than destroying layout.
            if (!TryGetBounds(entity, out _))
                return true;

            var nearby = CollisionDetector.CollectNearbyEntities(entity, btr, tr, originalHeight);
            if (!CollisionDetector.HasAnyCollision(entity, nearby))
            {
                Log.Debug("CollisionResolver: {Handle} clean", entity.Handle);
                return true;
            }

            Log.Information("CollisionResolver: {Handle} colliding, resolving (origH={H:F2})",
                entity.Handle, originalHeight);

            var saved = SaveEntityState(entity);

            // Always restore original height before attempts
            SetEntityHeight(entity, originalHeight);
            Mark(entity);

            // Strategy 1: wrap (MText)
            if (entity is MText mtext)
            {
                if (TryWrapMText(mtext, btr, tr, originalHeight))
                {
                    Log.Information("CollisionResolver: {Handle} resolved by wrap", entity.Handle);
                    return true;
                }
            }

            // Strategy 2: micro-nudge
            if (TryNudge(entity, btr, tr, originalHeight))
            {
                Log.Information("CollisionResolver: {Handle} resolved by nudge", entity.Handle);
                return true;
            }

            // Strategy 3: height scale (last resort, keep position from best attempt so far)
            if (TryScaleHeight(entity, btr, tr, originalHeight))
            {
                Log.Information("CollisionResolver: {Handle} resolved by scale H={H:F2}",
                    entity.Handle, GetEntityHeight(entity));
                return true;
            }

            // Strategy 4: DBText -> MText then wrap
            if (entity is DBText dbText && entity is not AttributeReference)
            {
                if (TryConvertDBTextToMText(dbText, btr, tr, originalHeight, out var convertedId))
                {
                    newEntityId = convertedId;
                    Log.Information("CollisionResolver: {Handle} converted DBText->MText", dbText.Handle);
                    return true;
                }
            }

            // Residual: keep best-effort scaled height at original position rather than a large failed nudge.
            RestoreEntityState(entity, saved);
            // Apply soft min height only if current still collides and is larger than min
            SetEntityHeight(entity, originalHeight);
            Mark(entity);
            nearby = CollisionDetector.CollectNearbyEntities(entity, btr, tr, originalHeight);
            if (CollisionDetector.HasAnyCollision(entity, nearby))
            {
                // One final gentle scale toward min ratio
                TryScaleHeight(entity, btr, tr, originalHeight);
            }

            nearby = CollisionDetector.CollectNearbyEntities(entity, btr, tr, originalHeight);
            bool clean = !CollisionDetector.HasAnyCollision(entity, nearby);
            if (!clean)
                Log.Warning("CollisionResolver: {Handle} residual collision remains", entity.Handle);
            return clean;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "CollisionResolver: unexpected error for {Handle}", entity.Handle);
            return false;
        }
    }

    private static void Mark(Entity entity)
    {
        try
        {
            entity.RecordGraphicsModified(true);
            if (entity is MText mt) mt.RecordGraphicsModified(true);
        }
        catch { }
    }

    private static bool TryGetBounds(Entity entity, out Extents3d bounds)
    {
        bounds = default;
        try
        {
            bounds = CollisionDetector.GetCorrectedBounds(entity);
            return true;
        }
        catch
        {
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
        Mark(entity);
    }

    private static Point3d? GetPosition(Entity entity) => entity switch
    {
        AttributeReference a => a.Position,
        DBText t => t.Position,
        MText m => m.Location,
        _ => null
    };

    private static void SetPosition(Entity entity, Point3d p)
    {
        switch (entity)
        {
            case AttributeReference a: a.Position = p; break;
            case DBText t: t.Position = p; break;
            case MText m: m.Location = p; break;
        }
        Mark(entity);
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
            Mark(entity);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "CollisionResolver: failed to restore entity state");
        }
    }

    private static bool IsClean(Entity entity, BlockTableRecord btr, Transaction tr, double originalHeight)
    {
        try
        {
            Mark(entity);
            var nearby = CollisionDetector.CollectNearbyEntities(entity, btr, tr, originalHeight);
            return !CollisionDetector.HasAnyCollision(entity, nearby);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryWrapMText(MText mtext, BlockTableRecord btr, Transaction tr, double originalHeight)
    {
        try
        {
            mtext.TextHeight = originalHeight;
            Mark(mtext);

            if (!TryGetBounds(mtext, out var currentBounds))
                return false;

            double currentWidth = currentBounds.MaxPoint.X - currentBounds.MinPoint.X;
            double originalWidth = mtext.Width > 0 ? mtext.Width : currentWidth;
            if (currentWidth <= 0) return false;

            double savedWidth = mtext.Width;
            var candidates = new List<double>();

            if (originalWidth > 0)
            {
                candidates.Add(originalWidth);
                candidates.Add(originalWidth * 0.95);
                candidates.Add(originalWidth * 0.85);
                candidates.Add(originalWidth * 0.75);
            }
            candidates.Add(Math.Max(currentWidth * 0.85, originalHeight * 6));
            candidates.Add(Math.Max(currentWidth * 0.70, originalHeight * 5));
            candidates.Add(Math.Max(currentWidth * 0.55, originalHeight * 4));

            foreach (var raw in candidates.Distinct().OrderByDescending(x => x))
            {
                double wrapWidth = Math.Max(raw, WBC.MinMTextRectangleWidth);
                mtext.Width = wrapWidth;
                mtext.ColumnType = ColumnType.NoColumns;
                Mark(mtext);

                if (IsClean(mtext, btr, tr, originalHeight))
                {
                    Log.Information("MText {Handle}: wrapped width={W:F1}", mtext.Handle, wrapWidth);
                    return true;
                }
            }

            mtext.Width = savedWidth;
            Mark(mtext);
            return false;
        }
        catch (Exception ex)
        {
            Log.Debug("CollisionResolver: TryWrapMText failed for {Handle}: {Error}", mtext.Handle, ex.Message);
            return false;
        }
    }

    private static bool TryNudge(Entity entity, BlockTableRecord btr, Transaction tr, double originalHeight)
    {
        var origin = GetPosition(entity);
        if (!origin.HasValue) return false;

        SetEntityHeight(entity, originalHeight);
        Mark(entity);

        double step = originalHeight * 0.35;
        double maxNudge = originalHeight * WBC.MaxNudgeRatio;
        var dirs = new (double dx, double dy)[]
        {
            (1, 0), (-1, 0), (0, 1), (0, -1),
            (1, 1), (1, -1), (-1, 1), (-1, -1)
        };

        foreach (double scale in new[] { 1.0, 1.5, 2.0, 2.5 })
        {
            double dist = step * scale;
            if (dist > maxNudge) break;

            foreach (var (dx, dy) in dirs)
            {
                double len = Math.Sqrt(dx * dx + dy * dy);
                var p = new Point3d(
                    origin.Value.X + dx / len * dist,
                    origin.Value.Y + dy / len * dist,
                    origin.Value.Z);
                SetPosition(entity, p);

                if (IsClean(entity, btr, tr, originalHeight))
                    return true;
            }
        }

        SetPosition(entity, origin.Value);
        return false;
    }

    private static bool TryScaleHeight(Entity entity, BlockTableRecord btr, Transaction tr, double originalHeight)
    {
        double minHeight = originalHeight * WBC.MinHeightRatio;
        double hardMin = originalHeight * WBC.HardMinHeightRatio;
        double current = GetEntityHeight(entity);
        if (current <= 0) current = originalHeight;

        if (current <= minHeight)
        {
            if (!IsClean(entity, btr, tr, originalHeight) && current > hardMin)
            {
                SetEntityHeight(entity, hardMin);
                return IsClean(entity, btr, tr, originalHeight);
            }
            return IsClean(entity, btr, tr, originalHeight);
        }

        double lo = minHeight;
        double hi = current;
        double best = -1;

        for (int i = 0; i < WBC.MaxBinarySearchIterations; i++)
        {
            if (hi - lo < 0.005) break;
            double mid = (lo + hi) / 2.0;
            SetEntityHeight(entity, mid);

            if (IsClean(entity, btr, tr, originalHeight))
            {
                best = mid;
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        if (best > 0)
        {
            SetEntityHeight(entity, best);
            return true;
        }

        SetEntityHeight(entity, Math.Max(hardMin, minHeight));
        return IsClean(entity, btr, tr, originalHeight);
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
                TextHeight = originalHeight > 0 ? originalHeight : dbText.Height,
                TextStyleId = dbText.TextStyleId,
                Rotation = dbText.Rotation,
                Contents = dbText.TextString,
                ColumnType = ColumnType.NoColumns
            };

            Extents3d dbBounds;
            try { dbBounds = dbText.GeometricExtents; }
            catch { mtext.Dispose(); return false; }

            double textWidth = dbBounds.MaxPoint.X - dbBounds.MinPoint.X;
            double wrapWidth = Math.Max(textWidth * 0.85, originalHeight * 8);
            if (wrapWidth < WBC.MinMTextRectangleWidth) wrapWidth = WBC.MinMTextRectangleWidth;
            mtext.Width = wrapWidth;

            btr.AppendEntity(mtext);
            tr.AddNewlyCreatedDBObject(mtext, true);
            Mark(mtext);

            // Try wrap candidates if still colliding
            if (!IsClean(mtext, btr, tr, originalHeight))
            {
                if (!TryWrapMText(mtext, btr, tr, originalHeight) &&
                    !TryNudge(mtext, btr, tr, originalHeight) &&
                    !TryScaleHeight(mtext, btr, tr, originalHeight))
                {
                    mtext.Erase();
                    return false;
                }
            }

            if (!IsClean(mtext, btr, tr, originalHeight))
            {
                mtext.Erase();
                return false;
            }

            dbText.Erase();
            newMTextId = mtext.ObjectId;
            Log.Information("DBText {Handle} -> MText {NewHandle} (width={W:F1})",
                dbText.Handle, mtext.Handle, mtext.Width);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "CollisionResolver: TryConvertDBTextToMText failed for {Handle}", dbText.Handle);
            return false;
        }
    }
}
