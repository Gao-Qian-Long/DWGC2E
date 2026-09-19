using System.Security.Cryptography;
using System.Text;

namespace DwgTranslator.Core.Services;

/// <summary>Owns the per-user product data location and performs a non-destructive one-time migration.</summary>
public static class ProductDataDirectory
{
    private const string MarkerName = ".dwgc2e-localappdata-v1";
    private static readonly TimeSpan MigrationLockTimeout = TimeSpan.FromSeconds(30);

    public static string Initialize(string? installDirectory = null)
    {
        var overridden = Environment.GetEnvironmentVariable("DWGC2E_DATA_DIR");
        var target = NormalizeDirectory(string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DWGC2E")
            : overridden!);
        Directory.CreateDirectory(target);

        if (!string.IsNullOrWhiteSpace(overridden)) return target;
        var legacyRoaming = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DwgTranslator");
        MigrateOnce(target, legacyRoaming, installDirectory);
        return target;
    }

    internal static void MigrateOnce(string target, string? legacyRoaming, string? installDirectory)
    {
        target = NormalizeDirectory(target);
        Directory.CreateDirectory(target);
        EnsureSafeDestinationDirectory(target, target);

        using var mutex = new Mutex(false, BuildMutexName(target));
        var ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = mutex.WaitOne(MigrationLockTimeout);
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }

            if (!ownsMutex)
                throw new TimeoutException("等待用户数据迁移锁超时；为避免并发写入，启动已中止。");

            var marker = Path.Combine(target, MarkerName);
            if (File.Exists(marker)) return;

            // Existing roaming data is the user's most recent profile and therefore has priority.
            if (!string.IsNullOrWhiteSpace(legacyRoaming))
                CopyTreeWithoutOverwrite(legacyRoaming!, target, target);

            if (!string.IsNullOrWhiteSpace(installDirectory))
            {
                var install = NormalizeDirectory(installDirectory!);
                foreach (var file in new[] { "settings.json", "translation_tasks.json", "device-id.txt" })
                    CopyFileWithoutOverwrite(Path.Combine(install, file), Path.Combine(target, file), target);
                foreach (var directory in new[] { "accounts", "glossaries", "projects", "exports", "logs" })
                    CopyTreeWithoutOverwrite(Path.Combine(install, directory), Path.Combine(target, directory), target);
            }

            WriteMarkerAtomically(marker);
        }
        finally
        {
            if (ownsMutex) mutex.ReleaseMutex();
        }
    }

    private static void CopyTreeWithoutOverwrite(string source, string destination, string allowedDestinationRoot)
    {
        if (!Directory.Exists(source)) return;

        var sourceRoot = NormalizeDirectory(source);
        var destinationRoot = NormalizeDirectory(destination);
        if (PathsEqual(sourceRoot, destinationRoot) || IsWithin(destinationRoot, sourceRoot))
            throw new InvalidOperationException("用户数据迁移源目录不得等于或包含目标目录。");

        if (IsReparsePoint(sourceRoot)) return;
        CreateSafeDestinationDirectories(allowedDestinationRoot, destinationRoot);
        EnsureSafeDestinationDirectory(allowedDestinationRoot, destinationRoot);

        var pending = new Stack<string>();
        pending.Push(sourceRoot);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (IsReparsePoint(current)) continue;

            foreach (var file in Directory.EnumerateFiles(current))
            {
                if (IsReparsePoint(file)) continue;
                var relative = GetSafeRelativePath(sourceRoot, file);
                CopyFileWithoutOverwrite(file, Path.Combine(destinationRoot, relative), allowedDestinationRoot);
            }

            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                if (IsReparsePoint(directory)) continue;
                _ = GetSafeRelativePath(sourceRoot, directory);
                pending.Push(directory);
            }
        }
    }

    private static void CopyFileWithoutOverwrite(string source, string destination, string destinationRoot)
    {
        if (!File.Exists(source) || IsReparsePoint(source)) return;

        destinationRoot = NormalizeDirectory(destinationRoot);
        var destinationFull = Path.GetFullPath(destination);
        EnsureWithinRoot(destinationRoot, destinationFull);
        if (File.Exists(destinationFull)) return;

        var parent = Path.GetDirectoryName(destinationFull)
            ?? throw new InvalidOperationException("迁移目标文件缺少父目录。");
        CreateSafeDestinationDirectories(destinationRoot, parent);
        EnsureSafeDestinationDirectory(destinationRoot, parent);

        try
        {
            File.Copy(source, destinationFull, false);
        }
        catch (IOException) when (File.Exists(destinationFull))
        {
            // Another process created the same file after our preflight. Never overwrite it.
        }
    }

    private static void CreateSafeDestinationDirectories(string root, string directory)
    {
        root = NormalizeDirectory(root);
        directory = NormalizeDirectory(directory);
        EnsureWithinRoot(root, directory);

        var relative = Path.GetRelativePath(root, directory);
        var current = root;
        if (relative == ".") return;

        foreach (var segment in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current))
            {
                if (IsReparsePoint(current))
                    throw new IOException($"迁移目标包含链接或联接点，已拒绝写入：{current}");
                continue;
            }

            Directory.CreateDirectory(current);
            if (IsReparsePoint(current))
                throw new IOException($"迁移目标目录不是普通目录，已拒绝写入：{current}");
        }
    }

    private static void EnsureSafeDestinationDirectory(string root, string directory)
    {
        root = NormalizeDirectory(root);
        directory = NormalizeDirectory(directory);
        EnsureWithinRoot(root, directory);

        var relative = Path.GetRelativePath(root, directory);
        var current = root;
        if (IsReparsePoint(current))
            throw new IOException($"用户数据根目录不得是链接或联接点：{current}");
        if (relative == ".") return;

        foreach (var segment in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) && IsReparsePoint(current))
                throw new IOException($"迁移目标包含链接或联接点，已拒绝写入：{current}");
        }
    }

    private static string GetSafeRelativePath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".." || Path.IsPathRooted(relative) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("迁移源路径逃逸出允许目录。");
        return relative;
    }

    private static void EnsureWithinRoot(string root, string path)
    {
        root = NormalizeDirectory(root);
        var full = Path.GetFullPath(path);
        if (!PathsEqual(root, full) && !IsWithin(full, root))
            throw new IOException("迁移目标路径逃逸出用户数据目录。");
    }

    private static bool IsWithin(string candidate, string root)
    {
        var prefix = NormalizeDirectory(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return Path.GetFullPath(candidate).StartsWith(prefix, PathComparison);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(NormalizeDirectory(left), NormalizeDirectory(right), PathComparison);

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void WriteMarkerAtomically(string marker)
    {
        var temporary = marker + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(DateTime.UtcNow.ToString("O"));
                writer.Flush();
                stream.Flush(true);
            }
            File.Move(temporary, marker, false);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static string BuildMutexName(string target)
    {
        var normalized = NormalizeDirectory(target).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return "Local\\DWGC2E.ProductDataMigration." + hash;
    }

    private static string NormalizeDirectory(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}


