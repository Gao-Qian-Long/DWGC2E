using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DwgTranslator.Core.Models;

namespace DwgTranslator.Core.Services;

public enum ProjectSourceValidation
{
    Valid,
    Missing,
    Changed
}

public sealed class ProjectConflictException : IOException
{
    public ProjectConflictException(string projectId, long expectedRevision, long? actualRevision)
        : base(actualRevision.HasValue
            ? $"翻译项目“{projectId}”已被其他窗口更新（本地版本 {expectedRevision}，磁盘版本 {actualRevision.Value}）。"
            : $"翻译项目“{projectId}”已被其他窗口删除（本地版本 {expectedRevision}）。")
    {
        ProjectId = projectId;
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    public string ProjectId { get; }
    public long ExpectedRevision { get; }
    public long? ActualRevision { get; }
}

public interface ITranslationProjectStore
{
    TranslationProject Create(string name, string sourceLanguage, string targetLanguage, IReadOnlyCollection<TextEntity> entities);
    TranslationProject Load(string projectId);
    IReadOnlyList<TranslationProjectSummary> List(string? search = null);
    void Save(TranslationProject project);
    void Rename(string projectId, string name);
    void Delete(string projectId);
    ProjectSourceValidation ValidateSource(TranslationProjectDrawing drawing);
    void AppendExport(string projectId, TranslationProjectExport export);
}

/// <summary>账号目录内的版本化项目存储。每个项目独立、原子提交，不复制源图。</summary>
public sealed class TranslationProjectStore : ITranslationProjectStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);
    private readonly string _projectsRoot;

    public TranslationProjectStore(string accountDirectory)
    {
        if (string.IsNullOrWhiteSpace(accountDirectory)) throw new ArgumentException("账号目录不能为空。", nameof(accountDirectory));
        _projectsRoot = Path.Combine(Path.GetFullPath(accountDirectory), "projects");
    }

    public TranslationProject Create(string name, string sourceLanguage, string targetLanguage, IReadOnlyCollection<TextEntity> entities)
    {
        var project = new TranslationProject
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"翻译项目 {DateTime.Now:yyyy-MM-dd HHmm}" : name.Trim(),
            SourceLanguage = sourceLanguage ?? string.Empty,
            TargetLanguage = targetLanguage ?? string.Empty
        };
        foreach (var group in entities.Where(e => !string.IsNullOrWhiteSpace(e.SourceFilePath))
                     .GroupBy(e => Path.GetFullPath(e.SourceFilePath), StringComparer.OrdinalIgnoreCase))
        {
            var drawing = new TranslationProjectDrawing
            {
                SourcePath = group.Key,
                SourceFileName = Path.GetFileName(group.Key),
                SourceSha256 = Fingerprint(group.Key),
                Entries = group.Select(ToEntry).ToList()
            };
            EnsureUniqueHandles(drawing);
            project.Drawings.Add(drawing);
        }
        Save(project);
        return project;
    }

    public TranslationProject Load(string projectId)
    {
        var path = ProjectFile(projectId);
        return ReadProject(path, projectId);
    }

    public IReadOnlyList<TranslationProjectSummary> List(string? search = null)
    {
        if (!Directory.Exists(_projectsRoot)) return Array.Empty<TranslationProjectSummary>();
        var result = new List<TranslationProjectSummary>();
        foreach (var directory in Directory.EnumerateDirectories(_projectsRoot))
        {
            try
            {
                var project = Load(Path.GetFileName(directory));
                if (!string.IsNullOrWhiteSpace(search) && !project.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                    && !project.Drawings.Any(d => d.SourceFileName.Contains(search, StringComparison.OrdinalIgnoreCase))) continue;
                result.Add(new TranslationProjectSummary { Id = project.Id, Name = project.Name, ModifiedAtUtc = project.ModifiedAtUtc, DrawingCount = project.Drawings.Count, SourceLanguage = project.SourceLanguage, TargetLanguage = project.TargetLanguage });
            }
            catch { /* a corrupt project remains on disk and is excluded from the index */ }
        }
        return result.OrderByDescending(p => p.ModifiedAtUtc).ToArray();
    }

    public void Save(TranslationProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Validate(project, project.Id);
        var path = ProjectFile(project.Id);
        WithProjectLock(path, () => SaveLocked(project, path));
    }

    public void Rename(string projectId, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("项目名称不能为空。", nameof(name));
        var path = ProjectFile(projectId);
        WithProjectLock(path, () =>
        {
            var project = ReadProject(path, projectId);
            project.Name = name.Trim();
            SaveLocked(project, path);
        });
    }

    /// <summary>Removes one archived translation project. Source drawings and exported files are never touched.</summary>
    public void Delete(string projectId)
    {
        var path = ProjectFile(projectId);
        WithProjectLock(path, () =>
        {
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        });
    }

    public ProjectSourceValidation ValidateSource(TranslationProjectDrawing drawing)
    {
        if (!File.Exists(drawing.SourcePath)) return ProjectSourceValidation.Missing;
        return string.Equals(Fingerprint(drawing.SourcePath), drawing.SourceSha256, StringComparison.OrdinalIgnoreCase)
            ? ProjectSourceValidation.Valid : ProjectSourceValidation.Changed;
    }

    public void AppendExport(string projectId, TranslationProjectExport export)
    {
        ArgumentNullException.ThrowIfNull(export);
        var path = ProjectFile(projectId);
        WithProjectLock(path, () =>
        {
            var project = ReadProject(path, projectId);
            project.ExportHistory.Add(export);
            SaveLocked(project, path);
        });
    }

    private void SaveLocked(TranslationProject project, string path)
    {
        var fileExists = File.Exists(path);
        var actualRevision = fileExists ? ReadProject(path, project.Id).Revision : (long?)null;
        if ((fileExists && actualRevision != project.Revision) || (!fileExists && project.Revision != 0))
            throw new ProjectConflictException(project.Id, project.Revision, actualRevision);

        var previousRevision = project.Revision;
        var previousModifiedAtUtc = project.ModifiedAtUtc;
        project.Revision = checked(previousRevision + 1);
        project.ModifiedAtUtc = DateTime.UtcNow;
        Validate(project, project.Id);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, project, JsonOptions);
                stream.Flush(true);
            }
            SafeFileCommit.Commit(temporary, path, overwrite: fileExists);
        }
        catch
        {
            project.Revision = previousRevision;
            project.ModifiedAtUtc = previousModifiedAtUtc;
            throw;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static TranslationProject ReadProject(string path, string expectedId)
    {
        var json = File.ReadAllText(path);
        var project = JsonSerializer.Deserialize<TranslationProject>(json, JsonOptions)
            ?? throw new InvalidDataException("翻译项目为空。");
        Validate(project, expectedId);
        return project;
    }

    private static void WithProjectLock(string projectPath, Action action)
    {
        using var mutex = new Mutex(false, BuildMutexName(projectPath));
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(LockTimeout);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired) throw new IOException("等待翻译项目写入锁超时，请关闭其他正在操作该项目的窗口后重试。");
            action();
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }

    private static string BuildMutexName(string projectPath)
    {
        var canonicalPath = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPath)));
        return $"Local\\DWGC2E.TranslationProject.{hash}";
    }

    private string ProjectFile(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_'))
            throw new ArgumentException("项目 ID 无效。", nameof(id));
        return Path.Combine(_projectsRoot, id, "project.json");
    }

    private static TranslationProjectEntry ToEntry(TextEntity e) => new()
    {
        Handle = e.Handle, OriginalText = e.PlainText, RawText = e.RawText, EntityType = e.EntityType,
        TranslatedText = e.TranslatedText ?? string.Empty, Status = e.Status, Notes = e.Notes ?? string.Empty,
        GlossaryHit = e.GlossaryHit, ManuallyEdited = e.Status == TranslationStatus.Reviewed
    };

    private static void Validate(TranslationProject project, string expectedId)
    {
        if (project.SchemaVersion != 1 || project.Revision < 0 || string.IsNullOrWhiteSpace(project.Id) || project.Id != expectedId
            || project.Drawings == null || project.ExportHistory == null) throw new InvalidDataException("翻译项目结构或版本无效。");
        foreach (var drawing in project.Drawings)
        {
            if (string.IsNullOrWhiteSpace(drawing.SourcePath) || !Path.IsPathFullyQualified(drawing.SourcePath)
                || drawing.SourceSha256.Length != 64 || drawing.Entries == null) throw new InvalidDataException("翻译项目图纸记录无效。");
            EnsureUniqueHandles(drawing);
        }
    }

    private static void EnsureUniqueHandles(TranslationProjectDrawing drawing)
    {
        if (drawing.Entries.Any(e => string.IsNullOrWhiteSpace(e.Handle))
            || drawing.Entries.Select(e => e.Handle).Distinct(StringComparer.Ordinal).Count() != drawing.Entries.Count)
            throw new InvalidDataException("同一图纸包含空句柄或重复句柄。");
    }

    public static string Fingerprint(string path)
    {
        if (!File.Exists(path)) return string.Empty;
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
