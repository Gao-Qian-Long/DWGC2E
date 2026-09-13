using CommunityToolkit.Mvvm.ComponentModel;
using DwgTranslator.Core.Services;
using Serilog;
using System.IO;
using System.Text.Json;

namespace DwgTranslator.App.ViewModels;

/// <summary>
/// 设置页的配置项：全部直连 <see cref="DwgTranslator.Core.Models.AppConfig"/>，
/// 改动即写回 settings.json —— 界面开关不再是"看着能点、其实没接线"的摆设。
///
/// 注意：内存里的 API Key 是解密后的明文，写盘前必须重新加密（DPAPI），
/// 否则会把明文密钥写进配置文件。
/// </summary>
public partial class MainViewModel
{
    public bool ProtectDimensions
    {
        get => _config.ProtectDimensions;
        set { if (_config.ProtectDimensions == value) return; _config.ProtectDimensions = value; OnPropertyChanged(); PersistRuntimeConfig(); }
    }

    public bool ProtectTolerances
    {
        get => _config.ProtectTolerances;
        set { if (_config.ProtectTolerances == value) return; _config.ProtectTolerances = value; OnPropertyChanged(); PersistRuntimeConfig(); }
    }

    public bool ProtectModels
    {
        get => _config.ProtectModels;
        set { if (_config.ProtectModels == value) return; _config.ProtectModels = value; OnPropertyChanged(); PersistRuntimeConfig(); }
    }

    public bool GlossaryFirst
    {
        get => _config.GlossaryFirst;
        set { if (_config.GlossaryFirst == value) return; _config.GlossaryFirst = value; OnPropertyChanged(); PersistRuntimeConfig(); }
    }

    /// <summary>
    /// 开机启动。真实落点是注册表 Run 项（见 <see cref="Services.StartupRegistration"/>）：
    /// 写注册表失败时必须回滚开关并说明原因，不能让用户以为已经勾上了。
    /// </summary>
    public bool StartWithWindows
    {
        get => _config.StartWithWindows;
        set
        {
            if (_config.StartWithWindows == value) return;

            if (!Services.StartupRegistration.TrySetEnabled(value, out var error))
            {
                StatusMessage = $"开机启动设置失败：{error}";
                OnPropertyChanged(); // 把开关回滚到真实状态
                return;
            }

            _config.StartWithWindows = value;
            StatusMessage = value ? "已开启开机启动" : "已关闭开机启动";
            OnPropertyChanged();
            PersistRuntimeConfig();
        }
    }

    public bool AutoCheckUpdate
    {
        get => _config.AutoCheckUpdate;
        set { if (_config.AutoCheckUpdate == value) return; _config.AutoCheckUpdate = value; OnPropertyChanged(); PersistRuntimeConfig(); }
    }

    public bool OpenOutputFolderAfterExport
    {
        get => _config.OpenOutputFolderAfterExport;
        set { if (_config.OpenOutputFolderAfterExport == value) return; _config.OpenOutputFolderAfterExport = value; OnPropertyChanged(); PersistRuntimeConfig(); }
    }

    public bool BackupSourceBeforeWrite
    {
        get => _config.BackupSourceBeforeWrite;
        set { if (_config.BackupSourceBeforeWrite == value) return; _config.BackupSourceBeforeWrite = value; OnPropertyChanged(); PersistRuntimeConfig(); }
    }

    /// <summary>命名规则预览：按当前规则与目标语言算出真实文件名，让用户改规则前先看见结果。</summary>
    public string NamingPreviewText
    {
        get
        {
            try
            {
                var sample = new OutputPathResolver(_config).RenderFileName("总图.dwg", CurrentTargetLang, DateTime.Now);
                return "预览：" + sample;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Naming preview failed");
                return "预览：—";
            }
        }
    }

    /// <summary>输出命名规则（可在设置页直接编辑）。</summary>
    public string OutputNamingPatternText
    {
        get => _config.OutputNamingPattern;
        set
        {
            if (string.Equals(_config.OutputNamingPattern, value, StringComparison.Ordinal)) return;
            _config.OutputNamingPattern = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NamingPreviewText));
            PersistRuntimeConfig();
        }
    }

    /// <summary>重名文件处理策略：skip / rename / overwrite（默认 skip，绝不静默覆盖）。</summary>
    public string DuplicatePolicyText
    {
        get => _config.DuplicatePolicy;
        set
        {
            if (string.Equals(_config.DuplicatePolicy, value, StringComparison.Ordinal)) return;
            _config.DuplicatePolicy = value ?? "skip";
            OnPropertyChanged();
            PersistRuntimeConfig();
        }
    }

    /// <summary>把当前配置写回 settings.json（API Key 先加密，绝不写明文）。</summary>
    private DwgTranslator.Core.Models.AppConfig? _runtimeBaseline;
    private void PersistRuntimeConfig()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_settingsPath)) return;

            var toWrite = new DwgTranslator.Core.Models.AppConfig();
            foreach (var property in typeof(DwgTranslator.Core.Models.AppConfig).GetProperties())
            {
                if (!property.CanRead || !property.CanWrite) continue;
                property.SetValue(toWrite, property.GetValue(_config));
            }
            toWrite.DeepSeekApiKey = DwgTranslator.Core.Models.AppConfig.EncryptApiKey(_config.DeepSeekApiKey);
            toWrite.AuthTokenEncrypted = _config.AuthTokenEncrypted; // Session is already DPAPI-encrypted.

            DwgTranslator.Core.Services.SettingsStore.Update(_settingsPath, latest => {
                foreach (var property in typeof(DwgTranslator.Core.Models.AppConfig).GetProperties())
                {
                    if (!property.CanRead || !property.CanWrite || property.Name == nameof(toWrite.AuthTokenEncrypted)) continue;
                    if (_runtimeBaseline != null && Equals(property.GetValue(_config), property.GetValue(_runtimeBaseline))) continue;
                    property.SetValue(latest, property.GetValue(toWrite));
                }
                _config.AuthTokenEncrypted = latest.AuthTokenEncrypted;
            });
            _runtimeBaseline = JsonSerializer.Deserialize<DwgTranslator.Core.Models.AppConfig>(JsonSerializer.Serialize(_config));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to persist runtime settings");
            StatusMessage = "无法保存设置，请检查配置目录权限。";
        }
    }
    public bool MemoryOptimization
    {
        get => _config.MemoryOptimization;
        set
        {
            if (_config.MemoryOptimization == value) return;
            _config.MemoryOptimization = value;
            _taskOptions.MemoryOptimization = value;
            OnPropertyChanged();
            PersistRuntimeConfig();
        }
    }


    // ── 性能参数（写穿到任务层：TaskManager 持有的就是同一个 Options 实例，改完立即生效）──

    /// <summary>可选项与任务层的夹取范围一致，界面不给出"选了也不生效"的档位。</summary>
    public IReadOnlyList<int> LocalWorkerOptions { get; } = [1, 2, 3, 4, 6];

    public IReadOnlyList<int> AiConcurrencyOptions { get; } = [1, 2, 3, 4, 6, 8];

    public IReadOnlyList<int> MaxRetryOptions { get; } = [0, 1, 2, 3, 4, 5];

    /// <summary>本地并发：同时解析 / 写回的图纸数（1..6）。</summary>
    public int LocalWorkerCount
    {
        get => _config.LocalWorkerCount;
        set
        {
            var clamped = Math.Min(6, Math.Max(1, value));
            if (_config.LocalWorkerCount == clamped) return;
            _config.LocalWorkerCount = clamped;
            _taskOptions.LocalWorkerCount = clamped;
            OnPropertyChanged();
            PersistRuntimeConfig();
        }
    }

    /// <summary>AI 并发：单张图纸内部的翻译请求并发数（1..8）。</summary>
    public int AiConcurrency
    {
        get => _config.AiConcurrency;
        set
        {
            var clamped = Math.Min(8, Math.Max(1, value));
            if (_config.AiConcurrency == clamped) return;
            _config.AiConcurrency = clamped;
            _taskOptions.AiConcurrency = clamped;
            OnPropertyChanged();
            PersistRuntimeConfig();
        }
    }

    /// <summary>失败重试次数（0..5）。</summary>
    public int MaxRetryCount
    {
        get => _config.MaxRetryCount;
        set
        {
            var clamped = Math.Min(5, Math.Max(0, value));
            if (_config.MaxRetryCount == clamped) return;
            _config.MaxRetryCount = clamped;
            _taskOptions.MaxRetryCount = clamped;
            OnPropertyChanged();
            PersistRuntimeConfig();
        }
    }}
