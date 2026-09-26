#if GSTARCAD
using Gssoft.Gscad.DatabaseServices;
using Gssoft.Gscad.Geometry;
#else
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
#endif
using System.Runtime.CompilerServices;
using DwgTranslator.Core.Services;
using WBC = DwgTranslator.Core.Models.WritebackConstants;
namespace DwgTranslator.Cad.Replacement;
internal static class AvailableTextSpace
{
    private sealed class Obstacle
    {
        public ObjectId Id; public Extents3d Box; public Extents3d Ink; public bool Text; public string Content="";
        public Point3d? SegmentStart; public Point3d? SegmentEnd;
        public double TextHeight;
        /// <summary>
        /// True when the source geometry itself is a straight frame/cell boundary. Keep this semantic
        /// flag before transforms: a vertical line rotated with its block has a fat axis-aligned box,
        /// but it is still a hard boundary and must retain clearance.
        /// </summary>
        public bool HardBoundary;
        /// <summary>Rendered rectangle corners in world coordinates, when reconstructable.</summary>
        public Point3d[]? Corners;
    }

    /// <summary>An obstacle reduced to the two axes of the measurement frame.</summary>
    private readonly struct Span
    {
        public readonly double NearU, FarU, NearV, FarV; public readonly bool Text; public readonly double Clearance;
        public Span(double nearU, double farU, double nearV, double farV, bool text, double clearance)
        { NearU = nearU; FarU = farU; NearV = nearV; FarV = farV; Text = text; Clearance = clearance; }
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
        var owner=GetTextOwner(text,tr);
        if(owner==null || owner.IsFromExternalReference || owner.IsFromOverlayReference || text is AttributeReference hidden && hidden.Invisible)yield break;

        // First validate the entity in its own owning block. Then, for definition text, walk every
        // INSERT path that places that block into a parent and validate the transformed text against
        // the parent's geometry too. A definition-local audit alone misses the common case where a
        // longer translation grows out of a child block and into a title-block line or sibling block.
        var measured=MeasureForInterference(text);
        if(measured==null)yield break;
        var (ink,ownCorners)=measured.Value;

        foreach(var issue in FindIntersectionsAgainstOwner(
                    text,owner,tr,ink,ownCorners,baseline,text.ObjectId,null,1.0))
            yield return issue;

        if(owner.IsLayout)yield break;

        var path=new HashSet<ObjectId>{owner.ObjectId};
        foreach(var issue in FindAncestorIntersections(
                    text,owner,tr,ink,ownCorners,baseline,Matrix3d.Identity,path,0))
            yield return issue;
    }

    private static IEnumerable<string> FindAncestorIntersections(
        Entity text,BlockTableRecord childOwner,Transaction tr,
        Extents3d localInk,Point3d[]? localCorners,Extents3d? localBaseline,
        Matrix3d accumulated,HashSet<ObjectId> path,int depth)
    {
        if(depth>=8)yield break;

        foreach(var referenceId in GetDirectBlockReferences(childOwner))
        {
            var reference=OpenBlockReference(tr,referenceId);
            if(reference==null)continue;
            var parent=OpenOwnerBlock(tr,reference.OwnerId);
            if(parent==null || parent.IsFromExternalReference || parent.IsFromOverlayReference)continue;

            // We are walking from the inner definition outward. Point3d.TransformBy composes
            // nested inserts as outer * inner, matching GetObstacles' top-down traversal.
            // Therefore the newly discovered parent transform must PREPEND the accumulated path.
            var transform=reference.BlockTransform*accumulated;
            var ink=localInk;
            ink.TransformBy(transform);

            Point3d[]? corners=null;
            if(localCorners!=null)
            {
                corners=new Point3d[localCorners.Length];
                for(int i=0;i<localCorners.Length;i++)
                    corners[i]=localCorners[i].TransformBy(transform);
            }

            Extents3d? transformedBaseline=null;
            if(localBaseline.HasValue)
            {
                var sourceBaseline=localBaseline.Value;
                sourceBaseline.TransformBy(transform);
                transformedBaseline=sourceBaseline;
            }

            // Exclude the INSERT that carries this definition. Its definition geometry is the
            // target's own local world and has already been audited above. Attributes remain
            // independently visible because GetObstacles records them by their own ObjectId.
            string context="|INSTANCE="+reference.Handle+"|OWNER="+parent.Handle;
            foreach(var issue in FindIntersectionsAgainstOwner(
                        text,parent,tr,ink,corners,transformedBaseline,reference.ObjectId,context,PlanarScale(transform)))
                yield return issue;

            if(parent.IsLayout || !path.Add(parent.ObjectId))continue;
            foreach(var issue in FindAncestorIntersections(
                        text,parent,tr,localInk,localCorners,localBaseline,transform,path,depth+1))
                yield return issue;
            path.Remove(parent.ObjectId);
        }
    }

    private static List<ObjectId> GetDirectBlockReferences(BlockTableRecord definition)
    {
        var result=new List<ObjectId>();
        try
        {
            foreach(ObjectId id in definition.GetBlockReferenceIds(true,false))
                if(id.IsValid)result.Add(id);
        }
        catch(Exception ex)
        {
            Log.DebugCategorized("Layout","Block reference lookup failed for {Handle}: {Detail}",
                definition.Handle,ex.Message);
        }
        return result;
    }

    private static BlockReference? OpenBlockReference(Transaction tr,ObjectId id)
    {
        try{return tr.GetObject(id,OpenMode.ForRead,false) as BlockReference;}
        catch{return null;}
    }

    private static BlockTableRecord? OpenOwnerBlock(Transaction tr,ObjectId id)
    {
        try{return id.IsValid?tr.GetObject(id,OpenMode.ForRead,false) as BlockTableRecord:null;}
        catch{return null;}
    }

    /// <summary>
    /// Effective XY scale of a nested INSERT path. Clearance is expressed in rendered/world units,
    /// so a block scaled 2x must also keep 2x the source text's local clearance. Using the larger
    /// axis is intentionally conservative for non-uniformly scaled blocks.
    /// </summary>
    private static double PlanarScale(Matrix3d transform)
    {
        try
        {
            var origin=new Point3d(0,0,0).TransformBy(transform);
            var x=new Point3d(1,0,0).TransformBy(transform);
            var y=new Point3d(0,1,0).TransformBy(transform);
            var scale=Math.Max(origin.DistanceTo(x),origin.DistanceTo(y));
            return !double.IsNaN(scale) && !double.IsInfinity(scale) && scale>1e-9 ? scale : 1.0;
        }
        catch { return 1.0; }
    }

    private static IEnumerable<string> FindIntersectionsAgainstOwner(
        Entity text,BlockTableRecord owner,Transaction tr,
        Extents3d ink,Point3d[]? ownCorners,Extents3d? baseline,
        ObjectId excludedRoot,string? context,double clearanceScale)
    {
        foreach(var obstacle in GetObstacles(owner,tr))
        {
            if(obstacle.Id==excludedRoot)continue;
            var box=obstacle.Ink;
            bool hardBoundary=obstacle.HardBoundary || (!obstacle.Text &&
                (box.MaxPoint.X-box.MinPoint.X<.001 || box.MaxPoint.Y-box.MinPoint.Y<.001));

            // Existing overlap with labels or symbols may be intentional, but a straight cell or
            // frame line is a hard boundary. Let those thin boundaries reach the shrink resolver
            // even when the source label already crossed them.
            if(!hardBoundary && baseline.HasValue &&
               box.MaxPoint.X>baseline.Value.MinPoint.X+.01 && box.MinPoint.X<baseline.Value.MaxPoint.X-.01 &&
               box.MaxPoint.Y>baseline.Value.MinPoint.Y+.01 && box.MinPoint.Y<baseline.Value.MaxPoint.Y-.01)
                continue;

            double textHeight=(text is DBText heightDb?heightDb.Height
                :text is MText heightMText?heightMText.TextHeight:0)*Math.Max(clearanceScale,1e-9);
            double clearance=obstacle.Text
                ? WBC.InterTextClearance(Math.Max(textHeight,obstacle.TextHeight))
                : hardBoundary?WBC.GeometryClearance(textHeight):0;
            bool overlaps;
            if(hardBoundary && obstacle.SegmentStart.HasValue && obstacle.SegmentEnd.HasValue)
            {
                // Compare a transformed line segment with visible ink instead of treating its
                // axis-aligned bounding rectangle as filled geometry.
                // A rotated MText's axis-aligned bounds contain large empty corners. Testing
                // those corners as ink falsely makes an indicator's short line collide with
                // the translated label even when its oriented glyph rectangle is clear.
                overlaps=CollisionDetector.SegmentWithinClearance(
                    obstacle.SegmentStart.Value,obstacle.SegmentEnd.Value,
                    ownCorners ?? BoxCorners(ink),clearance);
            }
            else
            {
                bool inkOverlaps;
                if(obstacle.Text)
                {
                    // Keep a visible gap between distinct labels, using visible ink rather than an
                    // MText layout rectangle that may include empty paragraphs.
                    double inkDistance=CollisionDetector.MinimumDistance2D(ink,box,null,null);
                    double renderedDistance=CollisionDetector.MinimumDistance2D(ink,box,ownCorners,obstacle.Corners);
                    inkOverlaps=TextEnvelopeGeometry.ViolatesClearance(inkDistance,clearance)
                        && TextEnvelopeGeometry.ViolatesClearance(renderedDistance,clearance);
                }
                else inkOverlaps=!hardBoundary
                    ?!(box.MaxPoint.X<ink.MinPoint.X+.01 || box.MinPoint.X>ink.MaxPoint.X-.01 ||
                       box.MaxPoint.Y<ink.MinPoint.Y+.01 || box.MinPoint.Y>ink.MaxPoint.Y-.01)
                    :!(box.MaxPoint.X<ink.MinPoint.X-clearance || box.MinPoint.X>ink.MaxPoint.X+clearance ||
                       box.MaxPoint.Y<ink.MinPoint.Y-clearance || box.MinPoint.Y>ink.MaxPoint.Y+clearance);
                overlaps=inkOverlaps && (ownCorners==null || obstacle.Corners==null
                    || CollisionDetector.QuadsOverlap(ownCorners,obstacle.Corners));
            }
            if(!overlaps)continue;

            string content=text is DBText d?d.TextString:text is MText m?m.Text:"";
            bool duplicate=obstacle.Text && content==obstacle.Content &&
                ink.MinPoint.DistanceTo(box.MinPoint)<.01 && ink.MaxPoint.DistanceTo(box.MaxPoint)<.01;
            yield return (duplicate?"DUPLICATE":"CONFLICT")+"="+text.Handle+","+obstacle.Id.Handle+
                "|KIND="+(obstacle.Text?"TEXT":"GEOMETRY")+"|OBSTACLE="+box+(context??"");
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

    // Attribute positions already use the containing drawing coordinates. Their owner
    // is an INSERT, not a BlockTableRecord; applying its transform again is incorrect.
    private static BlockTableRecord? GetTextOwner(Entity text, Transaction tr)
    {
        var owner=tr.GetObject(text.OwnerId,OpenMode.ForRead);
        if(owner is BlockReference insert)
            owner=tr.GetObject(insert.OwnerId,OpenMode.ForRead);
        return owner as BlockTableRecord;
    }

    private static double GetTextHeight(Entity entity) => entity switch
    {
        DBText dbText => Math.Abs(dbText.Height),
        MText mtext => Math.Abs(mtext.TextHeight),
        _ => 0
    };

    private static double GetPlanarScale(Matrix3d transform)
    {
        var origin=Point3d.Origin.TransformBy(transform);
        var xUnit=new Point3d(1,0,0).TransformBy(transform);
        var yUnit=new Point3d(0,1,0).TransformBy(transform);
        return Math.Max(origin.DistanceTo(xUnit),origin.DistanceTo(yUnit));
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
                    Collect((Entity)tr.GetObject(child,OpenMode.ForRead),child,transform,depth+1);
                return;
            }
            try
            {
                if(e is Line line)
                {
                    AddSegment(line.StartPoint,line.EndPoint);
                }
                else if(e is Polyline poly)
                {
                    int count=poly.Closed ? poly.NumberOfVertices : poly.NumberOfVertices-1;
                    for(int i=0;i<count;i++)
                    {
                        if(Math.Abs(poly.GetBulgeAt(i))>1e-6){Add(e.GeometricExtents,false);return;}
                        AddSegment(poly.GetPoint3dAt(i),poly.GetPoint3dAt((i+1)%poly.NumberOfVertices));
                    }
                }
                else Add(CollisionDetector.GetCorrectedBounds(e),e is DBText || e is MText);
            }
            catch (Exception ex)
            {
                // Keep a conservative host rectangle if decomposition failed;
                // never mistake a failed glyph measurement for empty space.
                try
                {
                    var box=e.GeometricExtents;box.TransformBy(transform);
                    bool isText=e is DBText || e is MText;
                    list.Add(new Obstacle{Id=root,Box=box,Ink=box,Text=isText,HardBoundary=e is Line,
                        TextHeight=isText?GetTextHeight(e)*GetPlanarScale(transform):0,
                        Content=e is DBText d?d.TextString:e is MText m?m.Text:""});
                }
                catch { Log.DebugCategorized("Layout", "No extents for object {Handle}: {Detail}", e.Handle, ex.Message); }
            }
            void AddSegment(Point3d p,Point3d q)
            {
                var a=p.TransformBy(transform);
                var b=q.TransformBy(transform);
                var box=new Extents3d(
                    new Point3d(Math.Min(a.X,b.X),Math.Min(a.Y,b.Y),Math.Min(a.Z,b.Z)),
                    new Point3d(Math.Max(a.X,b.X),Math.Max(a.Y,b.Y),Math.Max(a.Z,b.Z)));
                list.Add(new Obstacle{Id=root,Box=box,Ink=box,HardBoundary=true,SegmentStart=a,SegmentEnd=b});
            }
            void Add(Extents3d box,bool text,bool hardBoundary=false){
                var ink=text?CollisionDetector.GetCorrectedBounds(e,false):box;
                var corners=text?CollisionDetector.TryGetOrientedCorners(e):null;
                double textHeight=text?GetTextHeight(e)*GetPlanarScale(transform):0;
                box.TransformBy(transform);ink.TransformBy(transform);
                if(corners!=null)
                    for(int i=0;i<corners.Length;i++) corners[i]=corners[i].TransformBy(transform);
                list.Add(new Obstacle{Id=root,Box=box,Ink=ink,Text=text,HardBoundary=hardBoundary,
                    TextHeight=textHeight,Content=e is DBText d?d.TextString:e is MText m?m.Text:"" ,Corners=corners});
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
    public static Extents3d Measure(Entity text,Extents3d original,Transaction tr,bool characterColumn,out double readingLength,bool preserveSourceFootprint=true)
    {
        readingLength=0;
        if(text is AttributeDefinition)return original;
        double rotation=text is DBText dt ? dt.Rotation : text is MText mt ? mt.Rotation : double.NaN;
        if(double.IsNaN(rotation))return original;
        bool vertical=characterColumn || Math.Abs(Math.Cos(rotation))<.001;

        var owner=GetTextOwner(text,tr);
        // Definition text and its sibling geometry share block-local coordinates.
        // Use the same cell/collision policy as layouts, without mixing insert transforms.
        if(owner==null || owner.IsFromExternalReference || owner.IsFromOverlayReference)return original;

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
            double clearance=obstacle.Text
                ? WBC.InterTextClearance(Math.Max(h,obstacle.TextHeight)) : 0;
            spans.Add(vertical
                ? new Span(box.MinPoint.Y,box.MaxPoint.Y,box.MinPoint.X,box.MaxPoint.X,obstacle.Text,clearance)
                : new Span(box.MinPoint.X,box.MaxPoint.X,box.MinPoint.Y,box.MaxPoint.Y,obstacle.Text,clearance));
        }

        var (uMin,uMax,vMin,vMax)=ComputeCorridor(text,lo,hi,cLo,cHi,h,spans);
        readingLength=uMax-uMin;
        var measured= vertical
            ? new Extents3d(new Point3d(vMin,uMin,original.MinPoint.Z),new Point3d(vMax,uMax,original.MaxPoint.Z))
            : new Extents3d(new Point3d(uMin,vMin,original.MinPoint.Z),new Point3d(uMax,vMax,original.MaxPoint.Z));
        return preserveSourceFootprint ? WidenToSourceFootprint(measured,original) : measured;
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
            double clearance=obstacle.Text
                ? WBC.InterTextClearance(Math.Max(h,obstacle.TextHeight)) : 0;
            spans.Add(new Span(box.uMin,box.uMax,box.vMin,box.vMax,obstacle.Text,clearance));
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
        double rowInset=Math.Max(h*.15,.05);
        // A bounded table row has usable height beyond the source glyph footprint.
        // Only grow between nearby borders spanning the source; never infer a cell
        // from a distant drawing frame or consume an unbounded blank region.
        if (text is MText)
        {
            double below=double.NegativeInfinity, above=double.PositiveInfinity;
            foreach (var edge in spans)
            {
                if (edge.Text || edge.FarV-edge.NearV >= .001 || edge.NearU > lo || edge.FarU < hi) continue;
                if (edge.FarV <= cmid) below=Math.Max(below,edge.FarV);
                else above=Math.Min(above,edge.NearV);
            }
            if (cmid-below <= h*4 && above-cmid <= h*4 && above-below > h*.3)
            {
                rowInset=Math.Max(h*.08,.05);
                bottom=below+rowInset; top=above-rowInset;
            }
        }

        foreach(var obstacle in spans)
        {
            double near=obstacle.NearU, far=obstacle.FarU;
            double b=obstacle.NearV, t=obstacle.FarV;

            // Inset from frame lines even when the original text already overhangs them.
            if(!obstacle.Text && far>=lo && near<=hi && t-b<.001)
            {
                // Dense title-block rows need a height-relative inset on each
                // edge, not 70% of the original font height removed from a cell.
                double frameInset=rowInset;
                if(t<=cmid)bottom=Math.Max(bottom,t+frameInset);
                else top=Math.Min(top,b-frameInset);
                continue;
            }
            // Text in a separate row is not a horizontal obstacle. The old generous
            // font-height margin treated the row above/below as overlapping and collapsed
            // every narrow process-card cell back to the source Chinese word's width.
            if(obstacle.Text && (t<=cLo+.001 || b>=cHi-.001))continue;
            if(t<cLo-margin || b>cHi+margin)continue;
            if(far<=lo+.001)
                left=Math.Max(left,obstacle.Text ? (far+lo)/2+obstacle.Clearance/2 : far+Math.Max(h*.15,.05));
            else if(near>=hi-.001)
                right=Math.Min(right,obstacle.Text ? (near+hi)/2-obstacle.Clearance/2 : near-Math.Max(h*.15,.05));
            else if(!obstacle.Text && far-near<.001)
            {
                if(far<mid)left=Math.Max(left,far+Math.Max(h*.15,.05));else right=Math.Min(right,near-Math.Max(h*.15,.05));
            }
            else if(obstacle.Text && near>lo+.01 && far<hi-.01 && text is DBText label &&
                System.Text.RegularExpressions.Regex.IsMatch(label.TextString,@"^[共第]\s+页$"))
            {
                // A separate page number lives in the original whitespace.
                // Do not translate across that reserved numeric slot.
                if(label.TextString.StartsWith("共"))left=Math.Max(left,far+obstacle.Clearance);
                else right=Math.Min(right,near-obstacle.Clearance);
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
