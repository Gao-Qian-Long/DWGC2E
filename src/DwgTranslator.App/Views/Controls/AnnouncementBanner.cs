using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DwgTranslator.App.ViewModels;
using Serilog;
namespace DwgTranslator.App.Views.Controls;

/// <summary>Bounded title-bar announcement ticker with persistent unread state.</summary>
public sealed class AnnouncementBanner : Border
{
    private readonly TextBlock _ticker = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly TranslateTransform _offset = new();
    private readonly System.Windows.Shapes.Ellipse _dot = new() { Width = 7, Height = 7, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly Border _viewport;
    private readonly Button _button;
    private CancellationTokenSource? _request;
    private Window? _owner;
    private AnnouncementWindow? _dialog;
    private DateTime _lastAttempt = DateTime.MinValue;
    private string _content = "";
    public string DisplayText { get; private set; } = "正在读取公告…";
    public bool HasUnread => _dot.Visibility == Visibility.Visible;
    public bool IsOpen { get => _dialog?.IsVisible == true; set { if (value) OpenAnnouncement(); else _dialog?.Close(); } }

    public AnnouncementBanner()
    {
        Height = 32; Width = 220; Background = Brushes.Transparent;
        _button = new Button { Width = double.NaN, Height = 32, Padding = new Thickness(6,0,6,0), HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = "查看公告" };
        _button.SetResourceReference(StyleProperty, "Button.Icon");
        var row = new Grid { Width = 200 }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) }); row.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = new Grid { Width = 20, Height = 22 };
        var bell = new System.Windows.Shapes.Path { Width = 16, Height = 16, Stretch = Stretch.Uniform, StrokeThickness = 1.5,
            Data = Geometry.Parse("M 3,13 L 3,7 C 3,0 15,0 15,7 L 15,13 L 17,15 L 1,15 Z M 7,18 Q 9,20 11,18") };
        bell.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Brush.TextSecondary");
        _dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Brush.Danger");
        icon.Children.Add(bell); icon.Children.Add(_dot); row.Children.Add(icon);
        _ticker.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        _ticker.RenderTransform = _offset;
        // Canvas gives the text its natural width; the parent clips it to the fixed region.
        var canvas = new Canvas { Height = 20, VerticalAlignment = VerticalAlignment.Center }; canvas.Children.Add(_ticker);
        _viewport = new Border { ClipToBounds = true, Child = canvas, Margin = new Thickness(6,0,0,0) };
        Grid.SetColumn(_viewport, 1); row.Children.Add(_viewport); _button.Content = row; Child = _button;
        _button.Click += (_, _) => OpenAnnouncement();
        _button.MouseEnter += (_, _) => _offset.BeginAnimation(TranslateTransform.XProperty, null);
        _button.MouseLeave += (_, _) => AnimateTicker();
        _viewport.SizeChanged += (_, _) => AnimateTicker();
        ShowMessage(DisplayText);
        Loaded += async (_, _) =>
        {
            _owner = Window.GetWindow(this);
            if (_owner != null) _owner.Activated += OwnerActivated;
            _refresh.Start(); await RefreshAsync();
        };
        Unloaded += (_, _) =>
        {
            _refresh.Stop(); _offset.BeginAnimation(TranslateTransform.XProperty, null);
            if (_owner != null) _owner.Activated -= OwnerActivated;
            _owner = null; _request?.Cancel(); _dialog?.Close();
        };
        DataContextChanged += async (_, _) => { if (IsLoaded) await RefreshAsync(); };
        _refresh.Tick += async (_, _) => await RefreshAsync();
    }
    private void OpenAnnouncement()
    {
        if (_dialog != null) { _dialog.Activate(); return; }
        _dialog = new AnnouncementWindow(Window.GetWindow(this), DisplayText);
        _dialog.Closed += (_, _) => { _dialog = null; _button.Focus(); };
        _dialog.Show();
        if (DataContext is MainViewModel vm && _content.Length > 0)
        { vm.MarkAnnouncementRead(_content); _dot.Visibility = Visibility.Collapsed; UpdateAccessibleName(); }
    }
    private async void OwnerActivated(object? sender, EventArgs e)
    { if (DateTime.UtcNow - _lastAttempt > TimeSpan.FromSeconds(15)) await RefreshAsync(); }
    public async Task RefreshAsync()
    {
        if (!IsLoaded || DataContext is not MainViewModel vm) return;
        _request?.Cancel();
        using var request = new CancellationTokenSource();
        _request = request; _lastAttempt = DateTime.UtcNow;
        try
        {
            var value = await vm.ReadSiteAnnouncementAsync(request.Token);
            if (!request.IsCancellationRequested)
            {
                _content = value;
                _dot.Visibility = vm.IsAnnouncementUnread(value) ? Visibility.Visible : Visibility.Collapsed;
                ShowMessage(string.IsNullOrWhiteSpace(value) ? "暂无公告" : value);
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!request.IsCancellationRequested)
            {
                _content = ""; _dot.Visibility = Visibility.Collapsed;
                ShowMessage("公告暂时无法加载，稍后自动重试");
                Log.Debug("公告加载失败：{ErrorType}", ex.GetType().Name);
            }
        }
        finally { if (ReferenceEquals(_request, request)) _request = null; }
    }
    private void ShowMessage(string value)
    {
        if (DisplayText != value) { DisplayText = value; _ticker.Text = value.Replace('\r', ' ').Replace('\n', ' '); AnimateTicker(); }
        else if (_ticker.Text.Length == 0) _ticker.Text = value;
        UpdateAccessibleName();
    }
    private void UpdateAccessibleName() => System.Windows.Automation.AutomationProperties.SetName(_button, (HasUnread ? "有未读公告，" : "查看公告，") + DisplayText);
    private void AnimateTicker()
    {
        _offset.BeginAnimation(TranslateTransform.XProperty, null);
        _ticker.Measure(new Size(double.PositiveInfinity, 32));
        var overflow = _ticker.DesiredSize.Width - _viewport.ActualWidth;
        if (!IsLoaded || !SystemParameters.ClientAreaAnimation || _button.IsMouseOver || overflow <= 0 || _viewport.ActualWidth <= 0) return;
        var duration = TimeSpan.FromSeconds(overflow / 32);
        var animation = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(-overflow, KeyTime.FromTimeSpan(duration + TimeSpan.FromSeconds(2))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(-overflow, KeyTime.FromTimeSpan(duration + TimeSpan.FromSeconds(4))));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(duration + TimeSpan.FromSeconds(4.1))));
        _offset.BeginAnimation(TranslateTransform.XProperty, animation);
    }
}
