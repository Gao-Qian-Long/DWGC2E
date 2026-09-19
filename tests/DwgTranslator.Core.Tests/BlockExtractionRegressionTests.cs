using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using ACadSharp.IO;
using DwgTranslator.Core.Services;

namespace DwgTranslator.Core.Tests;

public sealed class BlockExtractionRegressionTests
{
    [Theory]
    [InlineData(".dwg")]
    [InlineData(".dxf")]
    public void NestedAnonymousBlocksAndRepeatedLabelsAreExtractedOncePerHandle(string extension)
    {
        var root = Path.Combine(Path.GetTempPath(), "dwgc2e-blocks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var doc = new CadDocument();
            var parent = new BlockRecord("ProcessCard");
            var child = new BlockRecord("*U1");
            child.Flags = ACadSharp.Blocks.BlockTypeFlags.Anonymous;
            doc.BlockRecords.Add(parent);
            doc.BlockRecords.Add(child);
            child.Entities.Add(new ACadSharp.Entities.TextEntity { Value="断料", Height=5 });
            child.Entities.Add(new MText { Value="断料", Height=5 });
            parent.Entities.Add(new Insert(child));
            doc.Entities.Add(new Insert(parent));
            doc.Entities.Add(new Insert(parent));
            var path = Path.Combine(root,"blocks"+extension);
            if(extension==".dwg") DwgWriter.Write(path,doc); else DxfWriter.Write(path,doc,false);
            var bytes = File.ReadAllBytes(path);
            var items = new DwgReaderService().ExtractFromFile(path);
            Assert.Equal(2,items.Count);
            Assert.Equal(2,items.Select(x=>x.Handle).Distinct().Count());
            Assert.All(items,x=>Assert.Equal("断料",x.PlainText));
            Assert.All(items,x=>Assert.Equal("ProcessCard",x.BlockName));
            Assert.Equal(bytes,File.ReadAllBytes(path));
        }
        finally { Directory.Delete(root,true); }
    }
}
