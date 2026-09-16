using System.IO.Compression;
using System.Security.Cryptography;

// Read-only .NET bundle v6 reader for this acceptance probe, not a deployment tool.
// Format checked against dotnet/runtime v8.0.0 HostWriter.cs, Bundle/Manifest.cs,
// Bundle/FileEntry.cs and Bundle/Bundler.cs. No files are extracted or executed here.
internal sealed class InstalledBundle
{
    private readonly byte[] bytes;
    private readonly Dictionary<string, (int Offset,int Size,int Compressed)> entries = new(StringComparer.Ordinal);
    public InstalledBundle(string executable)
    {
        bytes = File.ReadAllBytes(executable);
        var signature = Convert.FromHexString("8B1202B96A612038727B930214D7A03213F5B9E6EFAE3318EE3B2DCE24B36AAE");
        int marker = bytes.AsSpan().IndexOf(signature);
        if (marker < 8) throw new InvalidDataException("Bundle signature missing");
        long header = BitConverter.ToInt64(bytes, marker-8);
        if (header < 0 || header >= bytes.Length) throw new InvalidDataException("Bundle header out of bounds");
        using var stream = new MemoryStream(bytes, false);
        stream.Position = header;
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt32() != 6 || reader.ReadUInt32() != 0) throw new InvalidDataException("Only bundle 6.0 is accepted");
        int count = reader.ReadInt32();
        if (count < 1 || count > 10000) throw new InvalidDataException("Invalid entry count");
        _ = reader.ReadString();
        stream.Position += 40; // deps and runtimeconfig locations, flags
        for (int i=0;i<count;i++) {
            long offset=reader.ReadInt64(), size=reader.ReadInt64(), compressed=reader.ReadInt64();
            byte type=reader.ReadByte(); string name=reader.ReadString();
            long stored=compressed>0?compressed:size;
            if(offset<0||size<0||size>128*1024*1024||compressed<0||stored>bytes.Length||offset>bytes.Length-stored)
                throw new InvalidDataException("Invalid entry bounds");
            if(type==1) entries.Add(name,(checked((int)offset),checked((int)size),checked((int)compressed)));
        }
    }
    public string ExecutableHash => Convert.ToHexString(SHA256.HashData(bytes));
    public byte[]? ReadAssembly(string name)
    {
        if(!entries.TryGetValue(name,out var entry)) return null;
        if(entry.Compressed==0) return bytes.AsSpan(entry.Offset,entry.Size).ToArray();
        using var input=new MemoryStream(bytes,entry.Offset,entry.Compressed,false);
        using var inflater=new DeflateStream(input,CompressionMode.Decompress);
        var result=new byte[entry.Size];
        inflater.ReadExactly(result);
        if(inflater.ReadByte()!=-1)throw new InvalidDataException("Expanded size mismatch");
        return result;
    }
}
