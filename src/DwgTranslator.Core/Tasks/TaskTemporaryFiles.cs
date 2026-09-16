using System.Diagnostics;
using System.Globalization;
using Serilog;

namespace DwgTranslator.Core.Tasks;

// Housekeeping only: never changes the committed queue or recovery evidence.
internal static class TaskTemporaryFiles
{
    private const int InspectionLimit = 128;
    private const int DeletionLimit = 16;
    private static readonly string Owner = GetOwner();

    private static string GetOwner()
    {
        using var process = Process.GetCurrentProcess();
        return process.Id.ToString(CultureInfo.InvariantCulture) + "-" +
            process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
    }

    internal static string CreatePath(string storePath) =>
        storePath + ".owner-" + Owner + "-" + Guid.NewGuid().ToString("N") + ".tmp";

    internal static void Cleanup(string storePath, DateTime utcNow)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(storePath))!;
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
            var prefix = Path.GetFileName(storePath) + ".owner-";
            var cutoff = utcNow.AddDays(-1);
            var removed = 0;
            // No recursion, wildcard store names, unbounded scan, or legacy-file guessing.
            foreach (var candidate in Directory.EnumerateFiles(directory).Take(InspectionLimit))
            {
                if (removed >= DeletionLimit) break;
                var name = Path.GetFileName(candidate);
                if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".tmp", StringComparison.Ordinal)) continue;
                var fields = name[prefix.Length..^4].Split('-');
                if (fields.Length != 3 || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0 ||
                    !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var started) || started <= 0 || started > utcNow.Ticks ||
                    fields[2].Length != 32 || !Guid.TryParseExact(fields[2], "N", out _)) continue;
                try
                {
                    if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(candidate)), directory, StringComparison.OrdinalIgnoreCase)) continue;
                    var file = new FileInfo(candidate);
                    if ((file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.ReadOnly)) != 0 || file.LastWriteTimeUtc > cutoff) continue;
                    if (!OwnerEnded(pid, started)) continue;
                    // Open the existing file exclusively; a locked file is retained. Delete on
                    // close targets this opened file rather than doing a later name-based delete.
                    using (new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                    removed++;
                }
                catch (Exception ex) { Log.Debug(ex, "保留暂不可清理的任务临时文件：{Path}", candidate); }
            }
            if (removed > 0) Log.Information("已清理 {Count} 个过期且所属进程已结束的任务临时文件", removed);
        }
        catch (Exception ex) { Log.Debug(ex, "跳过任务临时文件清理：{Path}", storePath); }
    }

    private static bool OwnerEnded(int pid, long started)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited || process.StartTime.ToUniversalTime().Ticks != started;
        }
        catch (ArgumentException) { return true; } // PID no longer exists.
        catch { return false; } // Inaccessible process is not evidence that it ended.
    }
}
