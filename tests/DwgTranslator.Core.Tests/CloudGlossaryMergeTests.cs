using DwgTranslator.Core.Api;
using DwgTranslator.Core.Services;
using Xunit;

namespace DwgTranslator.Core.Tests;

public sealed class CloudGlossaryMergeTests
{
    private const string Id = "12345678-1234-4234-8234-123456789abc";
    private static CloudGlossaryEntry E(string source = "法兰", string target = "Flange", string? id = Id) =>
        new() { Id = id, Source = source, Target = target };
    private static IReadOnlyList<CloudGlossaryEntry> Resolve(CloudGlossaryEntry[] b, CloudGlossaryEntry[] l, CloudGlossaryEntry[] r) =>
        CloudGlossaryMerge.Resolve(CloudGlossaryMerge.Plan(b, l, r));

    [Fact] public void IndependentChangesAndAdditionsSurvive()
    {
        var result = Resolve([E()], [E(target: "Local"), E("本机新增", "Local new", null)],
            [E(), E("云端新增", "Remote new", Guid.NewGuid().ToString())]);
        Assert.Equal(new[] { "Local", "Local new", "Remote new" }, result.Select(x => x.Target));
        Assert.Equal(Id, result[0].Id); Assert.Null(result[1].Id);
    }

    [Fact] public void ConcurrentEditsRequireExplicitChoice()
    {
        var plan = CloudGlossaryMerge.Plan([E()], [E(target: "Local")], [E(target: "Remote")]);
        Assert.True(plan.Single().HasConflict);
        Assert.Throws<InvalidOperationException>(() => CloudGlossaryMerge.Resolve(plan));
        Assert.Equal("Local", CloudGlossaryMerge.Resolve(plan, new Dictionary<int, GlossaryMergeChoice> { [0] = GlossaryMergeChoice.Local }).Single().Target);
        Assert.Equal("Remote", CloudGlossaryMerge.Resolve(plan, new Dictionary<int, GlossaryMergeChoice> { [0] = GlossaryMergeChoice.Remote }).Single().Target);
    }

    [Fact] public void DeleteVersusEditIsExplicitAndRestorationDropsStaleId()
    {
        var plan = CloudGlossaryMerge.Plan([E()], [E(target: "Local")], []);
        Assert.True(plan.Single().HasConflict);
        Assert.Null(CloudGlossaryMerge.Resolve(plan, new Dictionary<int, GlossaryMergeChoice> { [0] = GlossaryMergeChoice.Local }).Single().Id);
        Assert.Empty(CloudGlossaryMerge.Resolve(plan, new Dictionary<int, GlossaryMergeChoice> { [0] = GlossaryMergeChoice.Remote }));
        var reverse = CloudGlossaryMerge.Plan([E()], [], [E(target: "Remote")]);
        Assert.True(reverse.Single().HasConflict);
    }

    [Fact] public void UnmodifiedRowsHonorDeletionFromEitherSide()
    {
        Assert.Empty(Resolve([E()], [], [E()]));
        Assert.Empty(Resolve([E()], [E()], []));
        Assert.Empty(Resolve([E()], [], []));
    }

    [Fact] public void LostSaveResponseMatchesNewRowBySourceAndKeepsServerId()
    {
        var result = Resolve([], [E(id: null)], [E()]);
        Assert.Equal(Id, result.Single().Id);
    }

    [Fact] public void FirstSyncDoesNotTreatAbsentLocalEntriesAsCloudDeletion()
    {
        var result = Resolve([], [], [E()]); Assert.Single(result);
        var plan = CloudGlossaryMerge.Plan([], [E(target: "Local", id: null)], [E()]);
        Assert.True(plan.Single().HasConflict);
    }

    [Fact] public void DifferentStableIdsWithSameSourceAreNotCollapsed()
    {
        Assert.Equal(2, Resolve([], [E(target: "One")], [E(target: "Two", id: Guid.NewGuid().ToString())]).Count);
    }

    [Fact] public void RenameKeepsIdentityAndAllMetadataParticipatesInConflict()
    {
        Action<CloudGlossaryEntry>[] edits = [e => e.Source = "New", e => e.Target = "New", e => e.Note = "Note",
            e => e.Category = "Category", e => e.Folder = "Folder", e => e.Enabled = false];
        foreach (var edit in edits)
        {
            var local = E(); edit(local);
            var plan = CloudGlossaryMerge.Plan([E()], [local], [E(target: "Remote")]);
            Assert.True(plan.Single().HasConflict);
            var saved = CloudGlossaryMerge.Resolve(plan, new Dictionary<int, GlossaryMergeChoice> { [0] = GlossaryMergeChoice.Local }).Single();
            Assert.Equal(Id, saved.Id); Assert.True(CloudGlossaryMerge.Equal(local, saved));
        }
    }

    [Fact] public void DuplicateIdentityAndAmbiguousFallbackAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => CloudGlossaryMerge.Plan([], [E(), E()], []));
        Assert.Throws<InvalidOperationException>(() => CloudGlossaryMerge.Plan([E()], [E(id: null), E(target: "Other", id: null)], []));
    }

    [Fact] public void PreviewIsDetachedFromOriginalAndResolvedEntriesAreDetached()
    {
        var local = E(target: "Local"); var plan = CloudGlossaryMerge.Plan([E()], [local], [E()]);
        local.Target = "Later";
        var result = CloudGlossaryMerge.Resolve(plan); Assert.Equal("Local", result.Single().Target);
        result[0].Target = "Modified result"; Assert.Equal("Local", plan[0].Local!.Target);
    }

    [Fact] public void ResultRejectsDuplicatePairsAndOverLimitUnion()
    {
        Assert.Throws<InvalidOperationException>(() => Resolve([], [E()], [E(id: Guid.NewGuid().ToString())]));
        var local = Enumerable.Range(0, 600).Select(i => E("local" + i, "value", null)).ToArray();
        var remote = Enumerable.Range(0, 600).Select(i => E("remote" + i, "value", null)).ToArray();
        Assert.Throws<InvalidOperationException>(() => Resolve([], local, remote));
    }

    [Fact] public void NullNotesAndEmptyNotesAreEquivalent()
    {
        var a = E(); var b = E(); b.Note = ""; Assert.True(CloudGlossaryMerge.Equal(a, b));
    }
}
