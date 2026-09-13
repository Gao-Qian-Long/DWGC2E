using System;
using System.Collections.ObjectModel;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace DwgTranslator.App.Views.Controls;

/// <summary>一条 Toast 的展示模型（颜色按语义给定，不在 XAML 里做状态判断）。</summary>
public sealed class ToastItem
{
    public string Message { get; init; } = string.Empty;
    public Brush Accent { get; init; } = Brushes.Gray;
}

/// <summary>
/// Toast 浮层宿主：挂在 MainWindow 的覆盖层里，由 <see cref="Services.ToastService"/> 调用。
/// 只负责显示与自动消失，不做任何业务判断——这样任何 ViewModel 都能安全地报告结果。
/// </summary>
public partial class ToastHost : UserControl
{
    private const int MaxVisible = 4;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(4);

    private readonly ObservableCollection<ToastItem> _items = new();

    public ToastHost()
    {
        InitializeComponent();
        ToastItems.ItemsSource = _items;
    }

    /// <summary>显示一条消息（必须从 UI 线程调用；ToastService 会负责调度）。</summary>
    public void Show(string message, Brush accent)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        _items.Add(new ToastItem { Message = message, Accent = accent });
        while (_items.Count > MaxVisible) _items.RemoveAt(0);

        var item = _items[^1];
        var timer = new DispatcherTimer { Interval = Lifetime };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _items.Remove(item);
        };
        timer.Start();
    }
}
