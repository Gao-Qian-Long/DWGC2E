using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace DwgTranslator.App.Views.Controls;

public sealed class ToastItem
{
    public string Message { get; init; } = string.Empty;
    public Brush Accent { get; init; } = Brushes.Gray;
    internal TimeSpan Remaining { get; set; } = TimeSpan.FromSeconds(4);
}

/// <summary>Bounded, non-overlay notification region. Hidden notifications retain their lifetime.</summary>
public partial class ToastHost : UserControl
{
    private readonly ObservableCollection<ToastItem> _items = new();
    private readonly Queue<ToastItem> _pending = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Window? _owner;
    public bool IsScoped { get; set; }
    public int MaximumVisible { get; set; } = 3;
    internal bool ScopeIsActive => IsScoped && IsLoaded && Parent is FrameworkElement { IsVisible: true };
    public Func<bool>? ShouldPause { get; set; }
    public int VisibleCount => _items.Count;
    public int PendingCount => _pending.Count;

    public ToastHost()
    {
        InitializeComponent();
        ToastItems.ItemsSource = _items;
        Visibility = Visibility.Collapsed;
        Loaded += (_, _) => { _owner = Window.GetWindow(this); if (IsScoped) Services.ToastService.RegisterScope(this); _timer.Start(); };
        Unloaded += (_, _) => { _timer.Stop(); Services.ToastService.UnregisterScope(this); _owner = null; };
        _timer.Tick += (_, _) => Tick();
    }

    public void Show(string message, Brush accent)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var existing = _items.Concat(_pending).FirstOrDefault(x => x.Message == message);
        if (existing != null) { existing.Remaining = TimeSpan.FromSeconds(4); return; }
        _pending.Enqueue(new ToastItem { Message = message, Accent = accent });
        Refresh();
    }

    private bool Paused => (!IsScoped && (_owner?.ActualHeight ?? 360) < 540) || (IsScoped && !ScopeIsActive) || _owner is { IsEnabled: false } || (ShouldPause?.Invoke() ?? false);
    private int Capacity => Math.Clamp(Math.Min(MaximumVisible, (_owner?.ActualHeight ?? 360) < 600 ? 1 : (_owner?.ActualHeight ?? 360) < 900 ? 2 : 3), 1, 3);
    private void Refresh()
    {
        if (Paused) { Visibility = Visibility.Collapsed; return; }
        // A shrinking window must never keep a tall stack at the expense of page actions.
        while (_items.Count > Capacity) { var item = _items[^1]; _items.RemoveAt(_items.Count - 1); _pending.Enqueue(item); }
        while (_items.Count < Capacity && _pending.TryDequeue(out var item)) _items.Add(item);
        Visibility = _items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    private void Tick()
    {
        if (!Paused)
            foreach (var item in _items.ToArray())
            {
                item.Remaining -= _timer.Interval;
                if (item.Remaining <= TimeSpan.Zero) _items.Remove(item);
            }
        Refresh();
    }
}
