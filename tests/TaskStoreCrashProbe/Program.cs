using System.Diagnostics;
using System.Text.Json;
using DwgTranslator.Core.Tasks;

const int CandidateCount = 96;
const int PayloadLength = 256 * 1024;
if (args.Length == 3 && args[0] == "--child")
{
    var path = Path.Combine(args[1], "tasks.json");
    var store = new JsonTaskStore(path);
    if (args[2] == "before") store.Save(PausedEnumeration());
    else
    {
        var payload = new string('x', PayloadLength);
        store.Save(Enumerable.Range(0, CandidateCount).Select(i => new TranslationTask("drawing.dwg")
        { Id = $"new-{i}", Status = TranslationTaskStatus.Completed, Error = payload }));
        if (store.LastSaveFailed) return 2;
        File.WriteAllText(Path.Combine(args[1], "committed"), "ok");
        Thread.Sleep(Timeout.Infinite);
    }
    return 0;
    IEnumerable<TranslationTask> PausedEnumeration()
    {
        File.WriteAllText(Path.Combine(args[1], "enumerating"), "ready");
        Thread.Sleep(Timeout.Infinite);
        yield return new TranslationTask("never.dwg");
    }
}

var root = Path.Combine(Path.GetTempPath(), "dwgc2e-crash-probe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var evidence = new List<object>();
try
{
    foreach (var stage in new[] { "before", "created", "created", "writing", "writing", "writing", "after" })
    {
        var folder = Path.Combine(root, evidence.Count.ToString());
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "tasks.json");
        var store = new JsonTaskStore(path);
        store.Save(new[] { new TranslationTask("drawing.dwg") { Id = "old", Status = TranslationTaskStatus.Completed, Error = "previous commit" } });
        Require(!store.LastSaveFailed, "initial save failed");
        var before = File.ReadAllBytes(path);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "--child", folder, stage }) start.ArgumentList.Add(argument);
        using var child = Process.Start(start) ?? throw new Exception("child did not start");
        long? observedTemporaryBytes = null;
        try
        {
            var timer = Stopwatch.StartNew();
            bool observed = false;
            while (timer.Elapsed < TimeSpan.FromSeconds(30) && !child.HasExited)
            {
                if (stage == "before") observed = File.Exists(Path.Combine(folder, "enumerating"));
                else if (stage == "after") observed = File.Exists(Path.Combine(folder, "committed"));
                else
                {
                    foreach (var temp in Directory.GetFiles(folder, "tasks.json.*.tmp"))
                    {
                        try { observedTemporaryBytes = new FileInfo(temp).Length; observed = stage == "created" || observedTemporaryBytes > 0; }
                        catch (FileNotFoundException) { }
                    }
                }
                if (observed) break;
                Thread.Yield();
            }
            Require(observed, $"did not observe {stage} boundary; child exit={child.HasExited}");
            // This handle was created above solely for the disposable probe, never the user's APP.
            child.Kill();
            Require(child.WaitForExit(10000), "child did not exit");
            var committed = File.ReadAllBytes(path);
            using var json = JsonDocument.Parse(committed);
            var loaded = new JsonTaskStore(path).Load();
            var isOld = committed.SequenceEqual(before);
            var isNew = loaded.Count == CandidateCount && loaded.Select((t, i) =>
                t.Id == $"new-{i}" && t.Status == TranslationTaskStatus.Completed && t.Error == new string('x', PayloadLength)).All(x => x);
            Require(isOld || isNew, "torn or mixed committed queue");
            if (stage == "before") Require(isOld, "before-save interruption changed committed queue");
            if (stage == "after") Require(isNew, "acknowledged commit was lost");
            var abandonedFiles = Directory.GetFiles(folder, "tasks.json.*.tmp");
            var abandoned = abandonedFiles.Length;
            var reopened = new JsonTaskStore(path);
            reopened.Save(loaded);
            Require(abandonedFiles.All(File.Exists), "recent abandoned candidates were removed before retention elapsed");
            foreach (var temp in abandonedFiles) File.SetLastWriteTimeUtc(temp, DateTime.UtcNow.AddDays(-2));
            reopened.Save(loaded);
            Require(!reopened.LastSaveFailed, "cannot save after interruption");
            Require(abandonedFiles.All(temp => !File.Exists(temp)), "expired candidates from terminated child were not cleaned");
            Require(reopened.Load().Count == loaded.Count, "post-restart queue changed");
            Require(Directory.GetFiles(folder, "*.recovery-*.json").Length == 0, "healthy committed queue incorrectly marked corrupt");
            evidence.Add(new { stage, observedTemporaryBytes, recovered = isOld ? "previous" : "new", records = loaded.Count, abandoned, expiredOrphansCleaned = true, restartSavePassed = true });
        }
        finally
        {
            if (!child.HasExited) { child.Kill(); child.WaitForExit(10000); }
        }
    }
    Console.WriteLine(JsonSerializer.Serialize(new { passed = evidence.Count, cases = evidence }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
finally
{
    // Only this process's freshly generated, private temporary directory is removed.
    Directory.Delete(root, recursive: true);
}
static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
