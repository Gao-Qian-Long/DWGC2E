#if GSTARCAD
using Gssoft.Gscad.DatabaseServices;
using Gssoft.Gscad.Geometry;
#else
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
#endif
using System.Runtime.CompilerServices;
namespace DwgTranslator.Cad.Replacement;
internal static class AvailableTextSpace
{
    private sealed class Obstacle { public ObjectId Id; public Extents3d Box; public Extents3d Ink; public bool Text; public string Content=""; }
    private static readonly ConditionalWeakTable<Transaction, Dictionary<ObjectId,List<Obstacle>>> Snapshots = new();
    public static void Prepare(BlockTableRecord owner, Transaction tr) => GetObstacles(owner,tr);
    public static void Refresh(Transaction tr) => Snapshots.Remove(tr);
    internal static IEnumerable<string> FindIntersections(Entity text,Transaction tr)
    {
        var owner=tr.GetObject(text.OwnerId,OpenMode.ForRead) as BlockTableRecord;
        if(owner==null || !owner.IsLayout || text is AttributeReference)yield break;
        var ink=CollisionDetector.GetCorrectedBounds(text,false);
        foreach(var obstacle in GetObstacles(owner,tr))
        {
            if(obstacle.Id==text.ObjectId)continue;
            var box=obstacle.Ink;
            if(box.MaxPoint.X < ink.MinPoint.X+.01 || box.MinPoint.X > ink.MaxPoint.X-.01 ||
                box.MaxPoint.Y < ink.MinPoint.Y+.01 || box.MinPoint.Y > ink.MaxPoint.Y-.01)continue;
            string content=text is DBText d ? d.TextString : text is MText m ? m.Text : "";
            bool duplicate=obstacle.Text && content==obstacle.Content &&
                ink.MinPoint.DistanceTo(box.MinPoint)<.01 && ink.MaxPoint.DistanceTo(box.MaxPoint)<.01;
            yield return (duplicate?"DUPLICATE":"CONFLICT")+"="+text.Handle+","+obstacle.Id.Handle+"|KIND="+(obstacle.Text?"TEXT":"GEOMETRY")+"|OBSTACLE="+box;
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
                catch { Log.Debug("Layout", "No extents for object {0}: {1}",e.Handle,ex.Message); }
            }
            void Add(Extents3d box,bool text){
                var ink=text?CollisionDetector.GetCorrectedBounds(e,false):box;
                box.TransformBy(transform);ink.TransformBy(transform);
                list.Add(new Obstacle{Id=root,Box=box,Ink=ink,Text=text,Content=e is DBText d?d.TextString:e is MText m?m.Text:""});
            }
        }
    }
    public static Extents3d Measure(Entity text,Extents3d original,Transaction tr,bool characterColumn=false)
    {
        if(text is AttributeReference || text is AttributeDefinition)return original;
        double rotation=text is DBText dt ? dt.Rotation : text is MText mt ? mt.Rotation : double.NaN;
        if(double.IsNaN(rotation))return original;
        bool vertical=characterColumn || Math.Abs(Math.Cos(rotation))<.001;
        if(!vertical && Math.Abs(Math.Sin(rotation))>=.001)return original;
        var owner=tr.GetObject(text.OwnerId,OpenMode.ForRead) as BlockTableRecord;
        if(owner==null || !owner.IsLayout)return original;
        double lo=vertical?original.MinPoint.Y:original.MinPoint.X, hi=vertical?original.MaxPoint.Y:original.MaxPoint.X;
        double cLo=vertical?original.MinPoint.X:original.MinPoint.Y, cHi=vertical?original.MaxPoint.X:original.MaxPoint.Y;
        double h=text is DBText d?d.Height:((MText)text).TextHeight;
        double margin=Math.Max(h*.35,.05),length=hi-lo;
        double left=lo-length,right=hi+length*3,bottom=cLo,top=cHi;
        double mid=(lo+hi)/2, cmid=(cLo+cHi)/2;
        foreach(var obstacle in GetObstacles(owner,tr))
        {
            if(obstacle.Id==text.ObjectId)continue;
            var box=obstacle.Box;
            double near=vertical?box.MinPoint.Y:box.MinPoint.X,far=vertical?box.MaxPoint.Y:box.MaxPoint.X;
            double b=vertical?box.MinPoint.X:box.MinPoint.Y,t=vertical?box.MaxPoint.X:box.MaxPoint.Y;
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
        // Never silently undo a clearance constraint to make an export pass.
        if(right<=left || top<=bottom)throw new InvalidOperationException("No clear text corridor: "+text.Handle);
        return vertical ? new Extents3d(new Point3d(bottom,left,original.MinPoint.Z),new Point3d(top,right,original.MaxPoint.Z))
            :new Extents3d(new Point3d(left,bottom,original.MinPoint.Z),new Point3d(right,top,original.MaxPoint.Z));
    }
}
