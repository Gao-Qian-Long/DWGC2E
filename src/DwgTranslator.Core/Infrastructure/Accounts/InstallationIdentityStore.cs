using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace DwgTranslator.Core.Services;

/// <summary>Owns the persisted installation identity. Never issues a temporary identity on failure.</summary>
public static class InstallationIdentityStore
{
    public static string GetOrCreate(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new IOException("设备标识目录无效。");
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToUpperInvariant())));
        using var mutex = new Mutex(false, "Local\\DWGC2E.Identity." + name);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("设备标识正在被其他实例使用，请稍后重试。");
            Directory.CreateDirectory(directory);
            if (File.Exists(fullPath))
            {
                var stored = File.ReadAllText(fullPath).Trim();
                if (!Guid.TryParse(stored, out _)) throw new IOException("设备标识文件损坏。为避免重复占用名额，请联系支持修复。");
                return stored;
            }
            var id = Guid.NewGuid().ToString("D");
            var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var bytes = Encoding.UTF8.GetBytes(id);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                File.Move(temporary, fullPath);
                return id;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }
}
