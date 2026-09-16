namespace DwgTranslator.Core.Services;

/// <summary>Never delete the previous output before the replacement is durable.</summary>
public static class SafeFileCommit
{
    public static void Commit(string temporary, string destination, bool overwrite)
    {
        if (!System.IO.File.Exists(temporary) || new System.IO.FileInfo(temporary).Length == 0)
            throw new System.IO.IOException("Output is empty or missing.");
        if (overwrite && System.IO.File.Exists(destination))
            System.IO.File.Replace(temporary, destination, null);
        else
            System.IO.File.Move(temporary, destination); // fails closed on a concurrent creator
    }
}
