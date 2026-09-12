#if GSTARCAD
using Gssoft.Gscad.DatabaseServices;
using Gssoft.Gscad.Geometry;
#else
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
#endif
using System.Runtime.CompilerServices;
using WBC = DwgTranslator.Core.Models.WritebackConstants;
namespace DwgTranslator.Cad.Replacement;
internal static class AvailableTextSpace
{
    private sealed class Obstacle
    {
        public ObjectId Id; public Extents3d Box; public Extents3d Ink; public bool Text; public string Content="";
        /// <summary>Rendered rectangle corners in world coordinates, when reconstructable.</summary>
        public Point3d[]? Corners;
    }

    /// <summary>An obstacle reduced to the two axes of the measurement frame.</summary>
    private readonly struct Span
    {
        public readonly double NearU, FarU, NearV, FarV; public readonly bool Text;
        public Span(double nearU, double farU, double nearV, double farV, bool text)
        { NearU = nearU; FarU = farU; NearV = nearV; FarV = farV; Text = text; }
    }

    private static readonly ConditionalWeakTable<Transaction, Dictionary<ObjectId,List<Obstacle>>> Snapshots = new();
    public static void Prepare(BlockTableRecord owner, Transaction tr) => GetObstacles(owner,tr);
    public static void Refresh(Transaction tr) => Snapshots.Remove(tr);

    /// <param name="baseline">
    /// The box the SOURCE text occupied. Obstacles intersecting it are not reported: the drawing
    /// already accepted that overlap, so blaming the translation for it would revert a label that
    /// is no worse than the text it replaces. A label anchored a fraction of a unit above its
    /// indicator symbol is the common case -- the rotated ink box necessarily reaches down into
    /// the symbol, and both the source and any translation do so equally.
    /// </param>
    internal static IEnumerable<string> FindIntersections(Entity text,Transaction tr,Extents3d? baseline=null)
    {
        var owner=tr.GetObject(text.OwnerId,OpenMode.ForRead) as BlockTableRecord;
        if(owner==null || !owner.IsLayout || text is AttributeReference)yield break;

        // Measuring the entity's ink can fail (host reports a rectangle as extents, exploded MText
        // has no glyphs, style height is zero). That used to escape into the caller and abort the
        // whole writeback; fall back to the geometric extents so the entity is still checked.
        var measured = MeasureForInterference(text);
        if (measured == null) yield break;
        var (ink, ownCorners) = measured.Value;

        foreach(var obstacle in GetObstacles(owner,tr))
        {
            if(obstacle.Id==text.ObjectId)continue;
            var box=obstacle.Ink;
            bool hardBoundary = !obstacle.Text &&
                (box.MaxPoint.X-box.MinPoint.X < .001 || box.MaxPoint.Y-box.MinPoint.Y < .001);

            // Existing overlap with labels or symbols may be intentional, but a straight cell or
            // frame line is a hard boundary. Let those thin boundaries reach the shrink resolver
            // even when the source label already crossed them.
            if(!hardBoundary && baseline.HasValue &&
               box.MaxPoint.X > baseline.Value.MinPoint.X+.01 && box.MinPoint.X < baseline.Value.MaxPoint.X-.01 &&
               box.MaxPoint.Y > baseline.Value.MinPoint.Y+.01 && box.MinPoint.Y < baseline.Value.MaxPoint.Y-.01)
                continue;   // the source text already overlapped this obstacle

            // Two rotated labels sit inside diagonal bounding boxes that overlap even when the
            // glyphs are far apart, so prefer the exact rectangle test whenever both shapes are
            // known. Anything else keeps the previous axis-aligned behaviour.
            //
            // The oriented shape an MText exposes here is its LAYOUT rectangle
            // (ActualWidth x ActualHeight), which counts empty leading paragraphs: these drawings
            // write a specification line as "{\fSimSun;\P}2.2KW", so its layout rectangle is two
            // lines tall and reaches through the label placed between it and the next line, while
            // its visible glyphs sit ten units away. Letting that rectangle introduce a clash
            // reverted every label pinned between two specification lines to Chinese, because the
            // baseline exemption is evaluated on the obstacle's INK box and could never clear an
            // obstacle the test never compared ink against. Ink against ink is the visible truth,
            // so the axis-aligned ink boxes are a necessary condition and the oriented test may
            // only REMOVE a conflict, never add one.
            double textHeight = text is DBText heightDb ? heightDb.Height
                : text is MText heightMText ? heightMText.TextHeight : 0;
            double clearance = hardBoundary ? WBC.GeometryClearance(textHeight) : 0;
            bool inkOverlaps = !hardBoundary
                ? !(box.MaxPoint.X < ink.MinPoint.X+.01 || box.MinPoint.X > ink.MaxPoint.X-.01 ||
                    box.MaxPoint.Y < ink.MinPoint.Y+.01 || box.MinPoint.Y > ink.MaxPoint.Y-.01)
                : !(box.MaxPoint.X < ink.MinPoint.X-clearance || box.MinPoint.X > ink.MaxPoint.X+clearance ||
                    box.MaxPoint.Y < ink.MinPoint.Y-clearance || box.MinPoint.Y > ink.MaxPoint.Y+clearance);
            bool overlaps = inkOverlaps && (ownCorners == null || obstacle.Corners == null
                || CollisionDetector.QuadsOverlap(ownCorners, obstacle.Corners));
            if(!overlaps)continue;

            string content=text is DBText d ? d.TextString : text is MText m ? m.Text : "";
            bool duplicate=obstacle.Text && content==obstacle.Content &&
                ink.MinPoint.DistanceTo(box.MinPoint)<.01 && ink.MaxPoint.DistanceTo(box.MaxPoint)<.01;
            yield return (duplicate?"DUPLICATE":"CONFLICT")+"="+text.Handle+","+obstacle.Id.Handle+"|KIND="+(obstacle.Text?"TEXT":"GEOMETRY")+"|OBSTACLE="+box;
        }
    }

    /// <summary>
    /// Ink box plus oriented rectangle of the entity being checked, or null when the host cannot
    /// measure it at all. Kept out of <see cref="FindIntersections"/> because a yield cannot appear
    /// inside a try/catch block.
    /// </summary>
    private static (Extents3d Ink, Point3d[]? Corners)? MeasureForInterference(Entity text)
    {
        try
        {
            return (CollisionDetector.GetCorrectedBounds(text, false), CollisionDetector.TryGetOrientedCorners(text));
        }
        catch (Exception ex)
        {
            Log.DebugCategorized("Layout", "Ink measurement failed for {Handle}: {Detail}", text.Handle, ex.Message);
            try { return (text.GeometricExtents, null); }
            catch { return null; }
        }
    }

    private static List<Obstacle> GetObstacles(BlockTableRecord owner, Transaction tr)
    {
        var cache=Snapshots.GetOrCreateValue(tr);
        if(cache.TryGetValue(owner.ObjectId,out var list)) return list;
        list=new List<Obstacle>();
        foreach(ObjectId id in owner)
        {
            if(tr.GetObject(id,OpenMode.ForRead) is not Entity e || !e.Visible || e is Viewport)continue;
            Collect(e,id,Matrix3d.Identity,0);
        }
        cache.Add(owner.ObjectId,list);return list;
        void Collect(Entity e,ObjectId root,Matrix3d transform,int depth)
        {
            if(!e.Visible || e is AttributeDefinition ad && (!ad.Constant || ad.Invisible))return;
            if(e is AttributeReference ar && ar.Invisible)return;
            if(e is DBText dt && string.IsNullOrWhiteSpace(dt.TextString))return;
            if(e is MText mt && string.IsNullOrWhiteSpace(mt.Text))return;
            if(e is BlockReference block && depth<8)
            {
                var def=(BlockTableRecord)tr.GetObject(block.BlockTableRecord,OpenMode.ForRead);
                foreach(ObjectId child in def)
                    if(tr.GetObject(child,OpenMode.ForRead) is Entity part)Collect(part,root,transform*block.BlockTransform,depth+1);
                foreach(ObjectId child in block.AttributeCollection)
                    Collect((Entity)tr.GetObject(child,OpenMode.ForRead),root,transform,depth+1);
                return;
            }
            try
            {
                if(e is Polyline poly)
                {
                    int count=poly.Closed ? poly.NumberOfVertices : poly.NumberOfVertices-1;
                    for(int i=0;i<count;i++)
                    {
                        if(Math.Abs(poly.GetBulgeAt(i))>1e-6){Add(e.GeometricExtents,false);return;}
                        var p=poly.GetPoint3dAt(i);var q=poly.GetPoint3dAt((i+1)%poly.NumberOfVertices);
                        Add(new Extents3d(new Point3d(Math.Min(p.X,q.X),Math.Min(p.Y,q.Y),Math.Min(p.Z,q.Z)),new Point3d(Math.Max(p.X,q.X),Math.Max(p.Y,q.Y),Math.Max(p.Z,q.Z))),false);
                    }
                }
                else Add(CollisionDetector.GetCorrectedBounds(e),e is DBText || e is MText);
            }
            catch (Exception ex)
            {
                // Keep a conservative host rectangle if decomposition failed;
                // never mistake a failed glyph measurement for empty space.
                try { var box=e.GeometricExtents;box.TransformBy(transform);list.Add(new Obstacle{Id=root,Box=box,Ink=box}); }
                catch { Log.DebugCategorized("Layout", "No extents for object {Handle}: {Detail}", e.Handle, ex.Message); }
            }
            void Add(Extents3d box,bool text){
                var ink=text?CollisionDetector.GetCorrectedBounds(e,false):box;
                var corners=text?CollisionDetector.TryGetOrientedCorners(e):null;
                box.TransformBy(transform);ink.TransformBy(transform);
                if(corners!=null)
                    for(int i=0;i<corners.Length;i++) corners[i]=corners[i].TransformBy(transform);
                list.Add(new Obstacle{Id=root,Box=box,Ink=ink,Text=text,Content=e is DBText d?d.TextString:e is MText m?m.Text:"" ,Corners=corners});
            }
        }
    }

    public static Extents3d Measure(Entity text,Extents3d original,Transaction tr,bool characterColumn=false)
        => Measure(text,original,tr,characterColumn,out _);

    /// <summary>
    /// Measures the space a translated entity may occupy.
    /// </summary>
    /// <param name="readingLength">
    /// The corridor's extent along the text's reading direction. Multi-line text must wrap within
    /// this, so callers that can wrap (MText) use it as the wrap width; deriving a wrap width from
    /// the axis-aligned bounding box of rotated text instead produces a column far too narrow and
    /// breaks words apart.
    /// </param>
    public static Extents3d Measure(Entity text,Extents3d original,Transaction tr,bool characterColumn,out double readingLength)
    {
        readingLength=0;
        if(text is AttributeReference || text is AttributeDefinition)return original;
        double rotation=text is DBText dt ? dt.Rotation : text is MText mt ? mt.Rotation : double.NaN;
        if(double.IsNaN(rotation))return original;
        bool vertical=characterColumn || Math.Abs(Math.Cos(rotation))<.001;

        var owner=tr.GetObject(text.OwnerId,OpenMode.ForRead) as BlockTableRecord;
        if(owner==null || !owner.IsLayout)return original;

        // Rotated text used to bail out here and keep its own bounding box as the allowance, which
        // bounds the translation to the exact area the source label occupied -- English in a
        // 4-character box can then only be squeezed into a thin column. Measure along the text's
        // own reading direction instead, exactly as the axis-aligned case does.
        if(!vertical && Math.Abs(Math.Sin(rotation))>=.001)
            return MeasureRotated(text,original,owner,tr,rotation,out readingLength);

        double lo=vertical?original.MinPoint.Y:original.MinPoint.X, hi=vertical?original.MaxPoint.Y:original.MaxPoint.X;
        double cLo=vertical?original.MinPoint.X:original.MinPoint.Y, cHi=vertical?original.MaxPoint.X:original.MaxPoint.Y;
        double h=text is DBText d?d.Height:((MText)text).TextHeight;

        var spans=new List<Span>();
        foreach(var obstacle in GetObstacles(owner,tr))
        {
            if(obstacle.Id==text.ObjectId)continue;
            var box=obstacle.Box;
            spans.Add(vertical
                ? new Span(box.MinPoint.Y,box.MaxPoint.Y,box.MinPoint.X,box.MaxPoint.X,obstacle.Text)
                : new Span(box.MinPoint.X,box.MaxPoint.X,box.MinPoint.Y,box.MaxPoint.Y,obstacle.Text));
        }

        var (uMin,uMax,vMin,vMax)=ComputeCorridor(text,lo,hi,cLo,cHi,h,spans);
        readingLength=uMax-uMin;
        var measured= vertical
            ? new Extents3d(new Point3d(vMin,uMin,original.MinPoint.Z),new Point3d(vMax,uMax,original.MaxPoint.Z))
            : new Extents3d(new Point3d(uMin,vMin,original.MinPoint.Z),new Point3d(uMax,vMax,original.MaxPoint.Z));
        return WidenToSourceFootprint(measured,original);
    }

    /// <summary>
    /// Never return an allowance tighter than the space the SOURCE text already occupied.
    ///
    /// These drawings routinely let a label cross a thin cell border, so the measured corridor can
    /// be smaller than the source's own box. Clamping to the corridor then forces the translation
    /// to be smaller than the text it replaces -- one title-block cell held a 7-character source
    /// that overhung its cell, and the English was squeezed to 45% to fit inside borders the
    /// Chinese never respected. Allowing at worst the source's own footprint keeps the aspect and
    /// the size, and any genuine clash is still caught by the rendered-interference pass.
    /// </summary>
    private static Extents3d WidenToSourceFootprint(Extents3d measured,Extents3d sourceFootprint) =>
        new Extents3d(
            new Point3d(Math.Min(measured.MinPoint.X,sourceFootprint.MinPoint.X),
                        Math.Min(measured.MinPoint.Y,sourceFootprint.MinPoint.Y),measured.MinPoint.Z),
            new Point3d(Math.Max(measured.MaxPoint.X,sourceFootprint.MaxPoint.X),
                        Math.Max(measured.MaxPoint.Y,sourceFootprint.MaxPoint.Y),measured.MaxPoint.Z));

    /// <summary>
    /// Measures the free corridor for text that is not a multiple of 90 degrees, by rotating the
    /// problem into the text's own frame: there the text is horizontal, co-rotated neighbours stay
    /// axis-aligned (so their rectangles are exact), and the axis-aligned corridor logic applies
    /// unchanged. The result is mapped back to world coordinates.
    /// </summary>
    private static Extents3d MeasureRotated(Entity text,Extents3d original,BlockTableRecord owner,Transaction tr,double rotation,out double readingLength)
    {
        readingLength=0;
        double cos=Math.Cos(rotation), sin=Math.Sin(rotation);
        double cx=(original.MinPoint.X+original.MaxPoint.X)/2, cy=(original.MinPoint.Y+original.MaxPoint.Y)/2;

        double ToU(double x,double y)=>(x-cx)*cos+(y-cy)*sin;
        double ToV(double x,double y)=>-(x-cx)*sin+(y-cy)*cos;
        Point3d ToWorld(double u,double v)=>new Point3d(cx+u*cos-v*sin, cy+u*sin+v*cos, original.MinPoint.Z);

        (double uMin,double uMax,double vMin,double vMax) LocalBox(Point3d[] corners)
        {
            double uMin=double.MaxValue,uMax=double.MinValue,vMin=double.MaxValue,vMax=double.MinValue;
            foreach(var p in corners)
            {
                double u=ToU(p.X,p.Y), v=ToV(p.X,p.Y);
                if(u<uMin)uMin=u; if(u>uMax)uMax=u;
                if(v<vMin)vMin=v; if(v>vMax)vMax=v;
            }
            return (uMin,uMax,vMin,vMax);
        }

        // The footprint of the label itself: its rendered rectangle when known, otherwise the
        // corners of the bounding box it currently occupies.
        var selfCorners=CollisionDetector.TryGetOrientedCorners(text) ?? BoxCorners(original);
        var self=LocalBox(selfCorners);
        double h=text is DBText d?d.Height:((MText)text).TextHeight;

        var spans=new List<Span>();
        foreach(var obstacle in GetObstacles(owner,tr))
        {
            if(obstacle.Id==text.ObjectId)continue;
            var box=LocalBox(obstacle.Corners ?? BoxCorners(obstacle.Box));
            spans.Add(new Span(box.uMin,box.uMax,box.vMin,box.vMax,obstacle.Text));
        }

        // A collapsed corridor here must not fail the whole job: before this measurement existed
        // rotated text simply kept its own bounding box, so fall back to exactly that instead of
        // letting the exception escape into the transaction.
        (double uMin,double uMax,double vMin,double vMax) corridor;
        bool collapsed;
        try
        {
            corridor = ComputeCorridor(text,self.uMin,self.uMax,self.vMin,self.vMax,h,spans);
            // The corridor rules clamp to the label's OWN interval when an obstacle overlaps its
            // footprint, so a successful measurement can still come back no larger than the source
            // text -- which then caps the translation at roughly the source's size and forces the
            // wrap width down to the diagonal bounding box. Treat that as collapsed too.
            collapsed = corridor.uMax - corridor.uMin <= (self.uMax - self.uMin) + 1e-6;
        }
        catch (InvalidOperationException) { collapsed = true; corridor = default; }

        if (collapsed)
        {
            // Extend along the reading direction by one more source length and keep the source's
            // own footprint across it. Real clashes are still resolved by the rendered-interference
            // pass, which bisects the largest clear size and only then restores the source text.
            double ownLength=Math.Max(self.uMax-self.uMin,0.1);
            readingLength=ownLength*2;
            var grown=new Extents3d(ToWorld(self.uMin-ownLength,self.vMin),ToWorld(self.uMin-ownLength,self.vMin));
            grown.AddPoint(ToWorld(self.uMax+ownLength,self.vMin));
            grown.AddPoint(ToWorld(self.uMax+ownLength,self.vMax));
            grown.AddPoint(ToWorld(self.uMin-ownLength,self.vMax));
            return WidenToSourceFootprint(grown,original);
        }

        readingLength=corridor.uMax-corridor.uMin;
        var result=new Extents3d(ToWorld(corridor.uMin,corridor.vMin),ToWorld(corridor.uMin,corridor.vMin));
        result.AddPoint(ToWorld(corridor.uMax,corridor.vMin));
        result.AddPoint(ToWorld(corridor.uMax,corridor.vMax));
        result.AddPoint(ToWorld(corridor.uMin,corridor.vMax));
        return WidenToSourceFootprint(result,original);
    }

    private static Point3d[] BoxCorners(Extents3d box) =>
    [
        new Point3d(box.MinPoint.X,box.MinPoint.Y,box.MinPoint.Z),
        new Point3d(box.MaxPoint.X,box.MinPoint.Y,box.MinPoint.Z),
        new Point3d(box.MaxPoint.X,box.MaxPoint.Y,box.MaxPoint.Z),
        new Point3d(box.MinPoint.X,box.MaxPoint.Y,box.MaxPoint.Z)
    ];

    /// <summary>
    /// One-dimensional corridor around the text along its reading axis (U) and across it (V).
    /// Shared by the axis-aligned and the rotated measurement so both apply the same rules.
    /// </summary>
    private static (double UMin,double UMax,double VMin,double VMax) ComputeCorridor(
        Entity text,double lo,double hi,double cLo,double cHi,double h,IReadOnlyList<Span> spans)
    {
        double margin=Math.Max(h*.35,.05),length=hi-lo;
        double left=lo-length,right=hi+length*3,bottom=cLo,top=cHi;
        double mid=(lo+hi)/2, cmid=(cLo+cHi)/2;

        foreach(var obstacle in spans)
        {
            double near=obstacle.NearU, far=obstacle.FarU;
            double b=obstacle.NearV, t=obstacle.FarV;

            // Inset from frame lines even when the original text already overhangs them.
            if(!obstacle.Text && far>=lo && near<=hi && t-b<.001)
            {
                // Dense title-block rows need a height-relative inset on each
                // edge, not 70% of the original font height removed from a cell.
                double frameInset=Math.Max(h*.15,.05);
                if(t<=cmid)bottom=Math.Max(bottom,t+frameInset);
                else top=Math.Min(top,b-frameInset);
                continue;
            }
            if(t<cLo-margin || b>cHi+margin)continue;
            if(far<=lo+.001)
                left=Math.Max(left,obstacle.Text ? (far+lo)/2+margin/2 : far+margin);
            else if(near>=hi-.001)
                right=Math.Min(right,obstacle.Text ? (near+hi)/2-margin/2 : near-margin);
            else if(!obstacle.Text && far-near<.001)
            {
                if(far<mid)left=Math.Max(left,far+margin);else right=Math.Min(right,near-margin);
            }
            else if(obstacle.Text && near>lo+.01 && far<hi-.01 && text is DBText label &&
                System.Text.RegularExpressions.Regex.IsMatch(label.TextString,@"^[共第]\s+页$"))
            {
                // A separate page number lives in the original whitespace.
                // Do not translate across that reserved numeric slot.
                if(label.TextString.StartsWith("共"))left=Math.Max(left,far+margin);
                else right=Math.Min(right,near-margin);
            }
            else {left=Math.Max(left,lo);right=Math.Min(right,hi);}
        }
        // A collapsed corridor must not abort the writeback. Throwing here used to leave the entity
        // in the caller's "unprocessed" set, which aborted the whole transaction and lost every
        // finished translation because of one label squeezed between two drawing lines. Clamp to a
        // minimal usable interval instead: the envelope fit and the interference pass still revert
        // this one label to its source text if it really does not fit.
        if (right <= left)
        {
            var centre = (lo + hi) / 2;
            var half = Math.Max((hi - lo) / 2, 0.05);
            left = centre - half;
            right = centre + half;
        }
        if (top <= bottom)
        {
            var centre = (cLo + cHi) / 2;
            var half = Math.Max((cHi - cLo) / 2, 0.05);
            bottom = centre - half;
            top = centre + half;
        }
        return (left,right,bottom,top);
    }
}
