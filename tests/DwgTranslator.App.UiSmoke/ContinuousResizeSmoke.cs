using System.Windows;
using System.Windows.Controls;
using DwgTranslator.App.ViewModels;
using DwgTranslator.App.Views;
using DwgTranslator.App.Views.Pages;

namespace UiSmoke;

public sealed partial class SmokeApp
{
    private async Task VerifyContinuousResizeAsync(MainWindow window, MainViewModel vm)
    {
        var previousPage = vm.CurrentPage;
        var previousSection = vm.SettingsSection;
        var oldWidth = window.Width;
        var oldHeight = window.Height;
        var oldMinimum = window.MinWidth;
        try
        {
            vm.SettingsSection = 0;
            foreach (var width in new[] { 800d, 900d, 1024d, 1060d, 1100d, 1160d, 1220d, 1300d, 1360d, 1380d, 1420d })
            {
                var requested = new Size(width, 720);
                if (requested.Width > SystemParameters.WorkArea.Width - 20
                    || requested.Height > SystemParameters.WorkArea.Height - 20)
                {
                    Console.WriteLine($"SKIP native resize sweep {requested}: monitor work area too small");
                    continue;
                }
                // 800/900 DIP are small-monitor fallback probes. Normal desktop use retains
                // the production minimum of 1024; restore it in finally.
                window.MinWidth = Math.Min(oldMinimum, width);
                FitNativeWindow(window, requested);
                await WaitForStableAsync(() => window.ActualWidth, $"resize sweep {width:F0}");
                foreach (var page in new[] { "translate", "batch", "glossary", "account", "settings" })
                {
                    vm.CurrentPage = page;
                    await NavIdleAsync();
                    var host = (FrameworkElement)window.FindName("PageHost");
                    Check(host.ActualWidth <= window.ActualWidth + 1,
                        $"resize sweep {width:F0} {page}: page host remains inside the native window");
                    if (page == "translate")
                    {
                        var translate = FindVisual<TranslatePage>(window);
                        var workspace = (FrameworkElement)translate.FindName("TranslationWorkspace");
                        var queue = (FrameworkElement)translate.FindName("QueueSurface");
                        var settings = (FrameworkElement)translate.FindName("TranslationSettingsScroll");
                        var queueRight = queue.TranslatePoint(new Point(queue.ActualWidth, 0), workspace).X;
                        var settingsRight = settings.TranslatePoint(new Point(settings.ActualWidth, 0), workspace).X;
                        Check(queueRight <= workspace.ActualWidth + 1 && settingsRight <= workspace.ActualWidth + 1,
                            $"resize sweep {width:F0}: translation panels remain inside the workspace (queue={queueRight:F1}, settings={settingsRight:F1}, workspace={workspace.ActualWidth:F1})");
                        if (workspace.ActualWidth < 1110)
                            Check(queue.ActualWidth >= workspace.ActualWidth - 24,
                                $"resize sweep {width:F0}: stacked queue uses the available width");
                    }
                    else if (page == "settings")
                    {
                        var settingsPage = FindVisual<SettingsPage>(window);
                        var viewport = (ScrollViewer)settingsPage.FindName("SettingsScroll");
                        var form = (FrameworkElement)settingsPage.FindName("SettingsContent");
                        Check(form.ActualWidth <= viewport.ActualWidth + 1,
                            $"resize sweep {width:F0}: settings form fits its viewport");
                    }
                    else if (page == "glossary" && width <= 900)
                    {
                        var glossary = FindVisual<GlossaryPage>(window);
                        var terms = (DataGrid)glossary.FindName("TermList");
                        var tableScroll = FindVisuals<ScrollViewer>(terms).FirstOrDefault();
                        Console.WriteLine($"GLOSSARY_NARROW width={width:F0} table={terms.ActualWidth:F1} columns={terms.Columns.Where(column => column.Visibility == Visibility.Visible).Sum(column => column.ActualWidth):F1} scrollable={tableScroll?.ScrollableWidth:F1} horizontal={tableScroll?.ComputedHorizontalScrollBarVisibility}");
                        var action = FindVisuals<System.Windows.Controls.Primitives.DataGridColumnHeader>(terms)
                            .FirstOrDefault(header => Equals(header.Content, "操作"));
                        Check(action is { IsVisible: true }
                              && action.TranslatePoint(new Point(action.ActualWidth, 0), terms).X <= terms.ActualWidth + 1
                              && terms.Columns.Single(column => Equals(column.Header, "适用方向")).Visibility == Visibility.Collapsed
                              && terms.Columns.Single(column => Equals(column.Header, "来源")).Visibility == Visibility.Collapsed,
                            $"resize sweep {width:F0}: glossary keeps editable columns and row actions on-screen");
                    }
                    if (width is 800 or 1024 or 1220 or 1420)
                        Capture(window, $"resize-sweep-{page}-{width:F0}");
                }
            }
        }
        finally
        {
            window.MinWidth = oldMinimum;
            FitNativeWindow(window, new Size(oldWidth, oldHeight));
            vm.CurrentPage = previousPage;
            vm.SettingsSection = previousSection;
            await NavIdleAsync();
        }
    }
}
