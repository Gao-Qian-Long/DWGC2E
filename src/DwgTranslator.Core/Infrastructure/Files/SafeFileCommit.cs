namespace DwgTranslator.Core.Services;

/// <summary>
/// 先落盘再替换：替换失败时旧输出必须还在，替换成功时旧输出保留为可回滚的 <c>.previous</c> 文件。
/// <para>
/// 旧实现用 <c>File.Replace(temp, destination, null)</c>，把"上一份输出"直接丢给操作系统删掉——
/// 用户在新输出有问题的现场没有任何回滚点。现在改为把旧输出搬到同目录的 <c>.previous</c>：
/// 替换失败时它就留在盘上（调用方把路径报给用户），成功时再删除。
/// </para>
/// </summary>
public static class SafeFileCommit
{
    /// <summary>旧输出的保留后缀。同名冲突时再退到 <c>.2.previous</c>、<c>.3.previous</c>……</summary>
    public const string RollbackSuffix = ".previous";

    /// <summary>
    /// 用 <paramref name="temporary"/> 替换 <paramref name="destination"/>（<paramref name="overwrite"/> 为假时只允许新建）。
    /// </summary>
    /// <returns>替换成功且旧输出已清理时为 null；旧输出仍留在盘上时返回它的完整路径。</returns>
    public static string? Commit(string temporary, string destination, bool overwrite)
    {
        if (!System.IO.File.Exists(temporary) || new System.IO.FileInfo(temporary).Length == 0)
            throw new System.IO.IOException("Output is empty or missing.");

        if (!overwrite || !System.IO.File.Exists(destination))
        {
            System.IO.File.Move(temporary, destination); // fails closed on a concurrent creator
            return null;
        }

        // 回滚点只有一个固定名字（.previous），而且要显式先落盘：不依赖 File.Replace 的隐式备份——
        // 目标被占用时它会直接失败，旧实现那种写法等于没有回滚点。
        var rollback = TryCreateRollbackPoint(destination);
        if (rollback == null)
            throw new System.IO.IOException(
                $"无法保留上一份输出的回滚点（同名文件被占用），本次覆盖已取消，文件未被改动：{destination}{RollbackSuffix}");

        // 旧输出已经有一份可回滚的副本，再做原子替换；替换失败时目标仍是旧输出，回滚点也在。
        System.IO.File.Replace(temporary, destination, null);

        // 替换成功，回滚点没有存在的必要时清理掉；清理不掉（被占用/只读）就把路径交回调用方。
        return TryDelete(rollback) ? null : rollback;
    }

    /// <summary>
    /// 把当前输出复制成回滚点。固定名字腾不出来时用带序号的后备名字：腾不出名字不是"可以丢回滚点"
    /// 的理由，但也不该让一次覆盖永远做不成。
    /// </summary>
    private static string? TryCreateRollbackPoint(string destination)
    {
        var rollback = destination + RollbackSuffix;
        if (TryDelete(rollback))
        {
            System.IO.File.Copy(destination, rollback, overwrite: false);
            return rollback;
        }

        for (var index = 2; index < 100; index++)
        {
            var alternative = destination + "." + index + RollbackSuffix;
            if (!System.IO.File.Exists(alternative))
            {
                System.IO.File.Copy(destination, alternative, overwrite: false);
                return alternative;
            }
        }
        return null;
    }

    private static string RollbackPath(string destination) => destination + RollbackSuffix;

    private static bool TryDelete(string path)
    {
        try
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            return true;
        }
        catch (System.IO.IOException) { return false; }
        catch (System.UnauthorizedAccessException) { return false; }
    }
}
