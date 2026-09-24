using DwgTranslator.Core.Logging;

namespace DwgTranslator.App.ViewModels;

/// <summary>
/// Wrapper around <see cref="LogEntry"/> for WPF binding with computed display properties.
/// </summary>
public class LogEntryViewModel
{
    private readonly LogEntry _entry;

    public LogEntryViewModel(LogEntry entry) => _entry = entry;

    public string Timestamp => _entry.Timestamp.ToString("HH:mm:ss.fff");
    public string LevelTag => _entry.LevelTag;
    public LogLevel Level => _entry.Level;
    public string Category => _entry.Category;
    public string Message => _entry.Message;
    public string? Exception => _entry.Exception;
    public string DisplayText => _entry.DisplayText;
    // The compact activity feed excludes exception stacks; full diagnostics remain in the log viewer.
    public string CompactDisplayText => $"[{Timestamp}] {Message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()}";
    public string SourceTag => _entry.SourceTag;
    public LogSource LogSource => _entry.LogSource;
    public string? CorrelationId => _entry.CorrelationId;
    public double? DurationMs => _entry.DurationMs;

    public string LevelColor => _entry.Level switch
    {
        LogLevel.Verbose => "#6E675D",
        LogLevel.Debug => "#6E675D",
        LogLevel.Information => "#60594F",
        LogLevel.Warning => "#835D27",
        LogLevel.Error => "#A34E49",
        LogLevel.Fatal => "#A34E49",
        _ => "#6E675D"
    };

    public string SourceColor => _entry.LogSource switch
    {
        LogSource.CadPlugin => "#60594F",
        LogSource.Translation => "#60594F",
        _ => "#60594F"
    };

    public string RowBackground => _entry.Level switch
    {
        LogLevel.Error => "Transparent",
        LogLevel.Fatal => "Transparent",
        LogLevel.Warning => "Transparent",
        _ => "Transparent"
    };
}
