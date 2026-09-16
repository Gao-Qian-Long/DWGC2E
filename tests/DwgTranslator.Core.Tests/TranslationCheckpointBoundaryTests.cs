using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using DwgTranslator.Core.Tasks;
using System.Text.Json;

namespace DwgTranslator.Core.Tests;

public sealed class TranslationCheckpointBoundaryTests
{
    [Fact]
    public async Task CompletedTranslationsAreOnDiskBeforeWriterStarts()
    {
        var root = Path.Combine(Path.GetTempPath(), "checkpoint-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.dwg");
            File.WriteAllText(source, "isolated drawing fixture");
            var storePath = Path.Combine(root, "tasks.json");
            var writer = new InspectingWriter(storePath);
            var config = new AppConfig { ExportDirectory = root, OutputNamingPattern = "{name}_translated", DuplicatePolicy = "rename", BackupSourceBeforeWrite = false, AllowOverwriteSource = false };
            using var manager = new TaskManager(new Reader(), null, new Translator(), writer, null,
                new JsonTaskStore(storePath), new TaskManagerOptions { LocalWorkerCount = 1, AiConcurrency = 1, MaxRetryCount = 0, MemoryOptimization = false }, config);
            manager.ConfigureRun("ZH", "EN", root, TaskWritebackMode.Offline);
            var task = manager.Enqueue(source);
            await manager.RunAsync(CancellationToken.None);
            Assert.True(writer.Called);
            // Read the captured bytes, never a mutable reference to the task or final file.
            var persisted = JsonSerializer.Deserialize<List<TranslationTask>>(writer.AtEntry!)!;
            var checkpoint = Assert.Single(persisted);
            Assert.Equal(task.Id, checkpoint.Id);
            Assert.Equal(2, checkpoint.SuccessfulTranslations.Count);
            Assert.All(checkpoint.SuccessfulTranslations, p => {
                Assert.Equal("Valve feedback", p.TranslatedText);
                Assert.Equal(TranslationStatus.Translated, p.Status);
                Assert.Equal(source, p.SourceFilePath);
            });
            Assert.Equal(2, checkpoint.SuccessfulTranslations.Select(p => p.Handle).Distinct().Count());
            Assert.False(string.IsNullOrEmpty(checkpoint.CheckpointSignature));
            Assert.Equal(TranslationTaskStatus.Completed, task.Status);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Reader : IDwgReaderService
    {
        public List<TextEntity> ExtractFromFile(string path) => new() {
            new() { Handle="A1", PlainText="阀门反馈", RawText="阀门反馈", SourceFilePath=path },
            new() { Handle="A2", PlainText="阀门反馈", RawText="阀门反馈", SourceFilePath=path }
        };
        public Dictionary<string,List<TextEntity>> ExtractFromFiles(IEnumerable<string> paths) => paths.ToDictionary(p=>p, ExtractFromFile);
    }
    private sealed class Translator : ITranslationService
    {
        public Task<string> TranslateAsync(string text,string sourceLang,string targetLang,CancellationToken cancellationToken=default) => Task.FromResult("Valve feedback");
        public Task<List<TranslationPair>> TranslateBatchAsync(List<TextEntity> entities,string sourceLang,string targetLang,CancellationToken cancellationToken=default) => TranslateBatchWithProgressAsync(entities,sourceLang,targetLang,null,cancellationToken);
        public Task<List<TranslationPair>> TranslateBatchWithProgressAsync(List<TextEntity> entities,string sourceLang,string targetLang,IProgress<TranslationPair>? progress=null,CancellationToken cancellationToken=default)
        {
            var pairs=entities.Select(e=>new TranslationPair { Handle=e.Handle,SourceFilePath=e.SourceFilePath,SourceText=e.PlainText,TranslatedText="Valve feedback",Status=TranslationStatus.Translated }).ToList();
            foreach(var pair in pairs) progress?.Report(pair);
            return Task.FromResult(pairs);
        }
    }
    private sealed class InspectingWriter(string storePath) : IDwgWriterService
    {
        public bool Called { get; private set; }
        public string? AtEntry { get; private set; }
        public CadWriteResult WriteTranslations(string sourceFilePath,string outputFilePath,List<TextEntity> entities,bool targetIsCjk=true,CancellationToken cancellationToken=default,WritebackOptions? options=null)
        {
            Called=true;
            AtEntry=File.ReadAllText(storePath);
            File.WriteAllText(outputFilePath,"isolated output");
            return new CadWriteResult { SuccessCount=entities.Count };
        }
    }
}
