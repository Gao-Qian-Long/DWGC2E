using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Serilog;

namespace DwgTranslator.App.Services;

/// <summary>
/// 开机启动。设置页那个开关以前只把布尔值写进 settings.json，注册表里什么都不写——
/// 用户勾上"开机启动"、重启电脑，程序当然不会自己起来。这里把它接到真正的落点：
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>（当前用户，不需要管理员权限）。
///
/// 值写入的是<b>当前进程的 exe 路径</b>（带引号，避免路径含空格时被截断），
/// 因此绿色版换目录后开关需要重新勾选一次——这也是为什么读取时会和当前路径比对：
/// 指向别处的旧值一律视为"未开机启动"，宁可显示真实状态也不显示一个假的勾。
/// </summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DwgTranslator";

    /// <summary>当前 exe 路径（单文件发布下就是 DwgTranslator.exe）。</summary>
    private static string CurrentExecutablePath
    {
        get
        {
            try
            {
                var path = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(path)) return path;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "无法读取 ProcessPath，退回主模块路径");
            }
            return Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
        }
    }

    /// <summary>注册表里是否已经指向"本程序当前所在的 exe"。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = key?.GetValue(ValueName) as string;
            if (string.IsNullOrWhiteSpace(value)) return false;

            var registered = value.Trim().Trim('"');
            var current = CurrentExecutablePath;
            return !string.IsNullOrWhiteSpace(current) &&
                   string.Equals(Path.GetFullPath(registered), Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "读取开机启动状态失败");
            return false;
        }
    }

    /// <summary>
    /// 写入或删除开机启动项。失败（权限、策略限制）时返回 false 并给出原因——
    /// 调用方必须把开关回滚并把原因告诉用户，不能"点了没反应"。
    /// </summary>
    public static bool TrySetEnabled(bool enabled, out string error)
    {
        error = string.Empty;
        var exePath = CurrentExecutablePath;
        if (enabled && string.IsNullOrWhiteSpace(exePath))
        {
            error = "无法确定程序路径";
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key == null)
            {
                error = "无法打开注册表启动项";
                return false;
            }

            if (enabled)
            {
                key.SetValue(ValueName, '"' + exePath + '"', RegistryValueKind.String);
                Log.Information("已开启开机启动：{Path}", exePath);
            }
            else
            {
                if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Information("已关闭开机启动");
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Warning(ex, "写入开机启动项失败（enabled={Enabled}）", enabled);
            return false;
        }
    }
}
