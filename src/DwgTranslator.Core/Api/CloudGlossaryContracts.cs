using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DwgTranslator.Core.Api;

/// <summary>One latest cloud version, not a persisted history or rollback snapshot.</summary>
public sealed class CloudGlossaryState
{
    public string Revision { get; }
    public IReadOnlyList<CloudGlossaryEntry> Entries { get; }
    internal string Session { get; }
    internal CloudGlossaryState(string revision, IReadOnlyList<CloudGlossaryEntry> entries, string session)
    { Revision = revision; Entries = entries; Session = session; }
}

public interface ICloudGlossaryClient
{
    Task<CloudGlossaryState> ReadCloudGlossaryAsync(CancellationToken ct = default);
    Task<CloudGlossaryState> SaveCloudGlossaryAsync(CloudGlossaryState basis, IReadOnlyList<CloudGlossaryEntry> entries, CancellationToken ct = default);
}

public sealed class CloudGlossaryException : Exception
{
    public string Code { get; }
    public CloudGlossaryException(string code, string message) : base(message) { Code = code; }
}
