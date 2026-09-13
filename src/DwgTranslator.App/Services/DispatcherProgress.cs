using System.Windows.Threading;

namespace DwgTranslator.App.Services;

/// <summary>Queues high-frequency producer updates without synchronously blocking the worker.</summary>
public sealed class DispatcherProgress<T>(Dispatcher dispatcher, Action<T> update) : IProgress<T>
{
    public void Report(T value)
    {
        if (dispatcher.CheckAccess())
        {
            update(value);
            return;
        }

        _ = dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => update(value)));
    }
}
