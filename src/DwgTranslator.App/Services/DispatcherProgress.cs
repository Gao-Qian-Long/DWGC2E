using System.Windows.Threading;

namespace DwgTranslator.App.Services;

/// <summary>
/// Acknowledges UI updates before the producer can complete. Unlike Progress
/// followed by BeginInvoke, no queued callback can overwrite the final status.
/// Use only when the producer runs on a worker and the UI awaits it asynchronously.
/// </summary>
public sealed class DispatcherProgress<T>(Dispatcher dispatcher, Action<T> update) : IProgress<T>
{
    public void Report(T value)
    {
        if (dispatcher.CheckAccess()) update(value);
        else dispatcher.Invoke(() => update(value));
    }
}
