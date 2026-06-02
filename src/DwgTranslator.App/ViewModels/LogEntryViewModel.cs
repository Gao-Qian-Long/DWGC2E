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
    public string SourceTag => _entry.SourceTag;
    public LogSource LogSource => _entry.LogSource;
    public string? CorrelationId => _entry.CorrelationId;
    public double? DurationMs => _entry.DurationMs;

    public string LevelColor => _entry.Level switch
    {
        LogLevel.Verbose => "#9E9E9E",
        LogLevel.Debug => "#78909C",
        LogLevel.Information => "#1976D2",
        LogLevel.Warning => "#FF9800",
        LogLevel.Error => "#E53935",
        LogLevel.Fatal => "#B71C1C",
        _ => "#757575"
    };

    public string SourceColor => _entry.LogSource switch
    {
        LogSource.CadPlugin => "#FF6F00",
        LogSource.Translation => "#7B1FA2",
        _ => "#546E7A"
    };

    public string RowBackground => _entry.Level switch
    {
        LogLevel.Error => "#FFF0F0",
        LogLevel.Fatal => "#FFEBEE",
        LogLevel.Warning => "#FFFDE7",
        _ => "Transparent"
    };
}
