using System.Text.Json;
using Serilog;

namespace DwgTranslator.Core.Services;

/// <summary>
/// 上次工作区的记录：语言对 + 上次打开的图纸列表，用于下次启动把工作区摆回来。
/// <para>
/// 与校对记录 / 翻译项目不同，这是一份<b>便利缓存</b>，语义上必须"读不出来就当没有"：
/// 文件缺失、被截断、被别的版本改写都只让本次启动退化成空工作区，绝不抛异常打断启动，
/// 也绝不让用户为一份可有可无的记录做任何决策。写失败同理，只记日志。
/// </para>
/// <para>
/// 落盘沿用 <see cref="SettingsStore"/> 的原子做法（同目录临时文件 + File.Move 覆盖）：
/// 进程被中断时留下的是旧记录或新记录，不会留下半截 JSON。写入前把图纸列表去重并截断到
/// <see cref="MaxDrawings"/> 张，避免这份文件随着使用无限增长。
/// </para>
/// </summary>
public sealed class WorkspaceSessionStore
{
    /// <summary>记录里保留的最大图纸数；超出的按最近使用的顺序丢弃。</summary>
    public const int MaxDrawings = 20;

    /// <summary>当前记录格式版本。读到的版本更高 = 别的版本写过，直接忽略而不是猜。</summary>
    public const int SchemaVersion = 1;

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string FilePath { get; }

    public WorkspaceSessionStore(string filePath) => FilePath = Path.GetFullPath(filePath);

    public sealed class Snapshot
    {
        public int Version { get; set; } = SchemaVersion;
        public DateTimeOffset SavedAtUtc { get; set; } = DateTimeOffset.UtcNow;
        public string SourceLanguage { get; set; } = string.Empty;
        public string TargetLanguage { get; set; } = string.Empty;
        public List<string> Drawings { get; set; } = new();
    }

    /// <summary>读取记录；没有可用记录时返回 null（含文件缺失、损坏、版本不符）。</summary>
    public Snapshot? Read()
    {
        lock (Gate)
        {
            if (!File.Exists(FilePath)) return null;
            try
            {
                var snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(FilePath), Options);
                if (snapshot == null)
                {
                    Log.Warning("上次工作区记录为空，本次按空工作区启动：{Path}", FilePath);
                    return null;
                }
                if (snapshot.Version != SchemaVersion)
                {
                    Log.Warning("上次工作区记录版本为 {Version}（当前 {Current}），已忽略：{Path}",
                        snapshot.Version, SchemaVersion, FilePath);
                    return null;
                }
                snapshot.Drawings = Normalize(snapshot.Drawings);
                return snapshot;
            }
            catch (Exception ex)
            {
                // 便利缓存不该让启动失败：损坏的记录按"没有记录"处理，下一次保存会覆盖它。
                Log.Warning(ex, "上次工作区记录无法读取，本次按空工作区启动：{Path}", FilePath);
                return null;
            }
        }
    }

    /// <summary>写入记录。失败只记日志，绝不打断调用方（关窗时的最后一次保存也走这里）。</summary>
    public void Save(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (Gate)
        {
            var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                snapshot.Version = SchemaVersion;
                snapshot.SavedAtUtc = DateTimeOffset.UtcNow;
                snapshot.Drawings = Normalize(snapshot.Drawings);
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, Options));
                File.Move(temporary, FilePath, true);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "上次工作区记录写入失败，下次启动将没有可恢复的工作区：{Path}", FilePath);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception ex) { Log.Debug(ex, "清理上次工作区临时文件失败：{Path}", temporary); }
            }
        }
    }

    /// <summary>删掉记录（清空工作区 / 用户关闭恢复时使用）。文件不存在或删不掉都不算错误。</summary>
    public void Clear()
    {
        lock (Gate)
        {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); }
            catch (Exception ex) { Log.Debug(ex, "删除上次工作区记录失败：{Path}", FilePath); }
        }
    }

    /// <summary>去重（大小写不敏感）、丢掉空路径、保留顺序并截断。</summary>
    private static List<string> Normalize(List<string>? drawings)
    {
        var result = new List<string>();
        if (drawings == null) return result;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drawing in drawings)
        {
            if (string.IsNullOrWhiteSpace(drawing)) continue;
            if (!seen.Add(drawing)) continue;
            result.Add(drawing);
            if (result.Count >= MaxDrawings) break;
        }
        return result;
    }
}
