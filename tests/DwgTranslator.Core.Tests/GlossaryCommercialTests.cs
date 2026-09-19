using System.Text.Json;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using DwgTranslator.Core.Infrastructure.Glossary;
namespace DwgTranslator.Core.Tests;
public sealed class GlossaryCommercialTests
{
    private static GlossaryEntry Term(string target = "Valve", GlossarySource kind = GlossarySource.User) => new() { Source=" 阀门 ", Target=target, SourceLang="ZH", TargetLang="EN", SourceKind=kind };
    [Fact] public void PendingDisabledAndReverseTermsNeverLeakIntoDirection()
    {
        var active=Term(); var pending=Term(); pending.SourceLang="";
        var reverse=Term(); reverse.SourceLang="EN"; reverse.TargetLang="ZH";
        var disabled=Term(); disabled.Enabled=false;
        Assert.Single(EffectiveGlossary.Resolve([active,pending,reverse,disabled],"ZH","EN"));
        Assert.True(pending.DirectionPending);
    }
    [Fact] public void PriorityIsIndependentOfCategoryAndConflictBlocksWholeDirection()
    {
        var system=Term("System",GlossarySource.System); var user=Term(); user.Category="other";
        Assert.Equal("Valve",Assert.Single(EffectiveGlossary.Resolve([system,user],"ZH","EN")).Target);
        Assert.Empty(EffectiveGlossary.Conflicts([system,user]));
        Assert.Throws<InvalidOperationException>(()=>EffectiveGlossary.Resolve([system,user,Term("VALVE")],"ZH","EN"));
        Assert.Empty(EffectiveGlossary.Resolve([user,Term("VALVE")],"EN","ZH"));
    }
    [Fact] public void SnapshotIsDetachedNormalizesAliasesAndMatchesLongestFirst()
    {
        var term=Term(); term.Source=" valve "; term.SourceLang="en-us"; term.TargetLang="zh-cn";
        var snapshot=EffectiveGlossary.Resolve([term],"EN","ZH"); term.Target="changed";
        Assert.Equal("Valve",Assert.Single(snapshot).Target);
        Assert.Single(EffectiveGlossary.Match("VALVE",snapshot));
    }
    [Fact] public void WorkspacePersistsEmptyCategoriesAndDefaultWithoutIdentityLoss()
    {
        var dir=Path.Combine(Path.GetTempPath(),"glossary-commercial-"+Guid.NewGuid()); Directory.CreateDirectory(dir);
        try {
            var path=Path.Combine(dir,"workspace-v2.json"); var entry=Term(); entry.Category="  ";
            GlossaryWorkspaceStore.Save(path,new(){Entries=[entry],Categories=["empty"]});
            var loaded=GlossaryWorkspaceStore.Load(path);
            Assert.Equal("默认分类",loaded.Entries[0].Category); Assert.Contains("empty",loaded.Categories);
            Assert.Equal(entry.LocalId,loaded.Entries[0].LocalId); Assert.DoesNotContain("",loaded.Categories);
        } finally { Directory.Delete(dir,true); }
    }
    [Fact] public void MigrationBacksUpRetainsDuplicatesAndIsIdempotent()
    {
        var dir=Path.Combine(Path.GetTempPath(),"glossary-migration-"+Guid.NewGuid()); Directory.CreateDirectory(dir);
        try {
            var legacy=Path.Combine(dir,"mechanical_zh_en.json");
            File.WriteAllText(legacy,JsonSerializer.Serialize(new[]{new GlossaryEntry{Source="阀",Target="Valve"},new GlossaryEntry{Source="阀",Target="Other"}}));
            var path=Path.Combine(dir,"workspace-v2.json"); var doc=GlossaryWorkspaceStore.Migrate(path,dir,Path.Combine(dir,"none"));
            Assert.Equal(2,doc.Entries.Count); Assert.All(doc.Entries,e=>Assert.False(e.DirectionPending));
            Assert.Equal(File.ReadAllText(legacy),File.ReadAllText(Path.Combine(dir,"migration-v2-backup","mechanical_zh_en.json")));
            Assert.Equal(doc.Entries.Select(e=>e.LocalId),GlossaryWorkspaceStore.Migrate(path,dir,dir).Entries.Select(e=>e.LocalId));
        } finally { Directory.Delete(dir,true); }
    }
}
