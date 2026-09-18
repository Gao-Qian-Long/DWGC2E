using System;
using System.Windows;
using System.Windows.Media;
using DwgTranslator.App.Views.Controls;
using Serilog;

namespace DwgTranslator.App.Services;

/// <summary>
/// 操作结果的统一出口（§29）：成功/提示/警告/错误走 Toast，危险操作才用 ConfirmDialog。
///
/// 设计要点：
///   · 静态入口，任何 ViewModel 都能调用，不需要把 UI 组件注入进去；
///   · 宿主（MainWindow 里的 ToastHost）没挂上时只写日志，绝不抛异常——
///     "提示显示不出来"不应该反过来打断业务流程；
///   · 自动切回 UI 线程，调用方可以在后台线程直接调。
/// </summary>
public static class ToastService
{
    private static ToastHost? _host;
    private static readonly List<ToastHost> Scopes = new();
    internal static void RegisterScope(ToastHost host) { if (!Scopes.Contains(host)) Scopes.Add(host); }
    internal static void UnregisterScope(ToastHost host) => Scopes.Remove(host);

    public static void Attach(ToastHost host) => _host = host;

    public static void Success(string message) => Show(message, ToastKind.Success);
    public static void Info(string message) => Show(message, ToastKind.Info);
    public static void Warning(string message) => Show(message, ToastKind.Warning);
    public static void Error(string message) => Show(message, ToastKind.Error);

    public enum ToastKind { Info, Success, Warning, Error }

    private static void Show(string message, ToastKind kind)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        var host = _host;
        if (host == null)
        {
            // 没有宿主（单元测试 / 启动早期）：落到日志，至少不丢信息。
            Log.Information("Toast({Kind}): {Message}", kind, message);
            return;
        }

        try
        {
            host.Dispatcher.Invoke(() =>
            {
                var destination = Scopes.LastOrDefault(scope => scope.ScopeIsActive && Window.GetWindow(scope)?.IsEnabled == true) ?? host;
                destination.Show(message, Accent(kind));
            });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "显示 Toast 失败，已忽略：{Message}", message);
        }
    }

    private static Brush Accent(ToastKind kind)
    {
        var key = kind switch
        {
            ToastKind.Success => "Brush.Success",
            ToastKind.Warning => "Brush.Warning",
            ToastKind.Error => "Brush.Danger",
            _ => "Brush.Primary"
        };

        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }
}
