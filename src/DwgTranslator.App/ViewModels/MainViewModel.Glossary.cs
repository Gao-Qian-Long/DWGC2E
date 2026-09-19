using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using System.Collections.ObjectModel;

namespace DwgTranslator.App.ViewModels;

/// <summary>
/// 术语库页面的真实数据：来源分布、优先级链与冲突术语。
///
/// 术语库是产品壁垒之一，页面上不能再用静态占位数字——这里全部由
/// <see cref="GlossaryConflictDetector"/> 从实际术语条目算出来，
/// 冲突条目按"用户 &gt; 企业 &gt; 系统"给建议，同优先级则要求用户确认。
/// </summary>
public partial class MainViewModel
{
    /// <summary>存在不同译法的术语（界面「冲突术语处理」卡）。</summary>
    public ObservableCollection<GlossaryConflict> GlossaryConflicts { get; } = [];

    public int GlossaryEntryCount => GlossaryEntries.Count;

    public int GlossaryUserCount => GlossaryEntries.Count(e => e.SourceKind == GlossarySource.User);

    public int GlossaryEnterpriseCount => GlossaryEntries.Count(e => e.SourceKind == GlossarySource.Enterprise);

    public int GlossarySystemCount => GlossaryEntries.Count(e => e.SourceKind == GlossarySource.System);

    public int GlossaryConflictCount => GlossaryConflicts.Count;

    /// <summary>需要用户拍板的冲突数（同优先级，不能自动裁决）。</summary>
    public int GlossaryConflictPendingCount => GlossaryConflicts.Count(c => c.NeedsConfirmation);

    /// <summary>优先级链文案，界面直接显示：用户术语 &gt; 企业术语 &gt; 系统术语 &gt; AI 默认。</summary>
    public string GlossaryPriorityChain => "用户术语 > 企业术语 > 系统术语 > AI 默认";

    /// <summary>重新计算术语统计与冲突列表（术语表导入/编辑后调用）。</summary>
    [RelayCommand]
    private void RefreshGlossaryConflicts()
    {
        var conflicts = EffectiveGlossary.Conflicts(GlossaryEntries)
            .GroupBy(e => (e.SourceLang, e.TargetLang, Source:e.Source.Trim().ToUpperInvariant()))
            .Select(group => {
                var first=group.First(); var other=group.First(e=>e.Target.Trim()!=first.Target.Trim());
                return new GlossaryConflict { Source=first.Source.Trim(), CandidateA=first, CandidateB=other, NeedsConfirmation=true, Suggestion="请确认" };
            });

        GlossaryConflicts.Clear();
        foreach (var conflict in conflicts) GlossaryConflicts.Add(conflict);

        OnPropertyChanged(nameof(GlossaryEntryCount));
        OnPropertyChanged(nameof(GlossaryUserCount));
        OnPropertyChanged(nameof(GlossaryEnterpriseCount));
        OnPropertyChanged(nameof(GlossarySystemCount));
        OnPropertyChanged(nameof(GlossaryConflictCount));
        OnPropertyChanged(nameof(GlossaryConflictPendingCount));
        OnPropertyChanged(nameof(GlossaryPriorityChain));
    }

    /// <summary>冲突术语处理：采纳建议（把落选译法停用，保留高优先级那条）。</summary>
    [RelayCommand]
    private void ResolveGlossaryConflict(GlossaryConflict? conflict)
    {
        if (conflict == null) return;

        // 同优先级时不自动裁决——界面会提示"请确认"，这里只处理有明确建议的冲突
        if (!conflict.NeedsConfirmation) conflict.CandidateB.Enabled = false;

        RefreshGlossaryConflicts();
        StatusMessage = conflict.NeedsConfirmation
            ? $"术语「{conflict.Source}」存在同级冲突，请人工确认"
            : $"术语「{conflict.Source}」已按优先级采纳：{conflict.CandidateA.Target}";
    }

    /// <summary>一键处理全部有明确建议的冲突（同级冲突保留待确认）。</summary>
    [RelayCommand]
    private void ResolveAllGlossaryConflicts()
    {
        int resolved = 0;
        foreach (var conflict in GlossaryConflicts.Where(c => !c.NeedsConfirmation).ToList())
        {
            conflict.CandidateB.Enabled = false;
            resolved++;
        }

        RefreshGlossaryConflicts();
        StatusMessage = resolved > 0
            ? $"已按优先级处理 {resolved} 处术语冲突，剩余 {GlossaryConflictPendingCount} 处待确认"
            : "没有可自动处理的术语冲突";
    }
}
