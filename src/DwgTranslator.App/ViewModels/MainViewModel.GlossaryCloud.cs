using System.Windows;
using DwgTranslator.Core.Api;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using Serilog;
namespace DwgTranslator.App.ViewModels;
public partial class MainViewModel
{
    public async Task UploadSelectedTermsAsync(IReadOnlyList<GlossaryEntry> selected)
    {
        if (!CanEditWorkspace || IsTermDrawerOpen || !RequireAccount()) return;
        if (_apiClient is not IIncrementalCloudGlossaryClient cloud) { TermFeedback="连接不支持增量同步，请升级服务端。"; return; }
        var terms=selected.Where(t=>t.SourceKind==GlossarySource.User && TermDraft.Contains(t)).ToList();
        if(terms.Count==0) { TermFeedback="所选没有用户术语；系统/企业术语请先复制。"; return; }
        if(!await SaveTermEditorAsync()) return;
        var context=CloudGlossaryContext; IsCloudGlossarySyncing=true;
        try
        {
            var latest=await cloud.ReadCloudGlossaryAsync(); if(context!=CloudGlossaryContext)return;
            ReconcilePendingUploads(latest);
            _cloudGlossaryBasis=latest; _cloudGlossaryContext=context;
            var updates=new List<CloudGlossaryEntry>(); var adopted=new Dictionary<string,CloudGlossaryEntry>();
            int added=0,changed=0,same=0,conflicts=0;
            foreach(var t in terms)
            {
                var local=ToCloudTerm(t);
                var remote=latest.Entries.FirstOrDefault(e=>e.Id==local.Id);
                if(remote==null && t.CloudId==null)
                {
                    var matches=latest.Entries.Where(e=>e.SourceLang==local.SourceLang && e.TargetLang==local.TargetLang && e.DirectionPending==local.DirectionPending && e.Source.Trim().Equals(local.Source,StringComparison.OrdinalIgnoreCase) && e.Target.Trim()==local.Target).ToList();
                    if(matches.Count>1) throw new InvalidOperationException("存在多个相同词条，无法安全关联，请先整理云端。");
                    remote=matches.SingleOrDefault(); if(remote!=null)local.Id=remote.Id;
                }
                _workspace.SyncBasis.TryGetValue(t.CloudId ?? "",out var basis);
                bool conflict=remote!=null && !CloudGlossaryMerge.Equal(remote,local) && (basis==null || !CloudGlossaryMerge.Equal(remote,basis));
                if(remote==null && t.CloudId!=null) conflict=true;
                if(conflict)
                {
                    conflicts++;
                    var answer=Views.PromptDialog.Show($"「{t.Source}」{t.DirectionText}\n本机：{t.Target}\n云端：{remote?.Target ?? "已删除"}\n请选择本次采用的版本。取消将不提交整个批次。","同步冲突",MessageBoxButton.YesNoCancel,confirmText:"采用本机",rejectText:"采用云端",cancelText:"取消批次");
                    if(answer==MessageBoxResult.Cancel)return;
                    if(answer==MessageBoxResult.No) { if(remote!=null)adopted[t.LocalId]=remote; else { TermFeedback="云端已删除；本机保留，请先在云端管理核对关联。"; return; } continue; }
                }
                if(remote==null) { added++; local.Id=t.CloudId ?? t.LocalId; }
                else if(CloudGlossaryMerge.Equal(local,remote)) { same++; adopted[t.LocalId]=remote; continue; }
                else changed++;
                updates.Add(local); adopted[t.LocalId]=local;
            }
            if(Views.PromptDialog.Show($"新增 {added}，更新 {changed}，不变 {same}，已处理冲突 {conflicts}，不参与 {selected.Count-terms.Count}。\n不参与项为系统/企业术语，需先复制为用户术语。\n只上传所选；云端未选中的条目不会删除。","确认上传所选",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
            // Persist recovery identities before sending: timeout is an unknown result, not a failed insert.
            foreach(var t in terms) if(adopted.TryGetValue(t.LocalId,out var pending) && updates.Any(e=>e.Id==pending.Id)) _workspace.PendingUploads[t.LocalId]=pending;
            PersistWorkspace(TermDraft.ToList());
            var saved=updates.Count==0?latest:await cloud.PatchCloudGlossaryAsync(latest,updates,Array.Empty<string>());
            if(context!=CloudGlossaryContext)return;
            foreach(var t in terms)
            {
                if(!adopted.TryGetValue(t.LocalId,out var candidate))continue;
                var remote=saved.Entries.SingleOrDefault(e=>e.Id==candidate.Id) ?? throw new InvalidOperationException("云端响应缺少提交的词条，请重新核对。");
                ApplyCloudTerm(t,remote); _workspace.SyncBasis[remote.Id!]=remote; _workspace.PendingUploads.Remove(t.LocalId);
            }
            _workspace.LastCheckedAt=DateTime.UtcNow; _cloudGlossaryBasis=saved;
            IsCloudGlossarySyncing=false;
            TermFeedback=await SaveTermEditorAsync()?"所选术语已核对并保存；其他云端词条未改变。":"云端已保存，但本机保存失败。请保留本机数据并重试核对，勿重复新增。";
        }
        catch(Exception ex) { Log.Warning(ex, "云端术语上传未确认，本机内容保留"); if(context==CloudGlossaryContext)TermFeedback="未确认同步完成，本机内容保留。请重新核对后重试。"+ex.Message; }
        finally { IsCloudGlossarySyncing=false; RefreshTermView(); }
    }
    public async Task OpenCloudWorkspaceAsync()
    {
        if(!CanEditWorkspace || IsTermDrawerOpen || !RequireAccount() || !await ConfirmLeaveGlossaryAsync())return;
        if(_apiClient is not IIncrementalCloudGlossaryClient cloud) {TermFeedback="当前连接不支持增量云端管理。";return;}
        EnsureWorkspace(); var context=CloudGlossaryContext; IsCloudGlossarySyncing=true;
        try
        {
            var latest=await cloud.ReadCloudGlossaryAsync(); if(context!=CloudGlossaryContext)return;
            ReconcilePendingUploads(latest);
            _cloudGlossaryBasis=latest; _cloudGlossaryContext=context; _workspace.LastCheckedAt=DateTime.UtcNow;
            foreach(var t in TermDraft.Where(t=>t.CloudId!=null && !latest.Entries.Any(e=>e.Id==t.CloudId))) { _workspace.SyncBasis.Remove(t.CloudId!); t.CloudId=null; }
            IsCloudGlossarySyncing=false;
            if(!await SaveTermEditorAsync())return;
            var choice=Views.GlossaryCloudWindow.Choose(latest.Entries,_config.ActiveAccountId ?? "当前账号",_workspace.LastCheckedAt.Value);
            if(choice==null || choice.Entries.Count==0 || context!=CloudGlossaryContext)return;
            if(choice.Delete)
            {
                if(Views.PromptDialog.Show($"仅删除云端所选 {choice.Entries.Count} 条，本机副本保留。确定？","删除云端",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
                IsCloudGlossarySyncing=true;
                var saved=await cloud.PatchCloudGlossaryAsync(latest,Array.Empty<CloudGlossaryEntry>(),choice.Entries.Select(e=>e.Id!).ToList());
                if(context!=CloudGlossaryContext)return;
                var ids=choice.Entries.Select(e=>e.Id).ToHashSet();
                foreach(var t in TermDraft.Where(t=>ids.Contains(t.CloudId))) { _workspace.SyncBasis.Remove(t.CloudId!); t.CloudId=null; }
                _cloudGlossaryBasis=saved; IsCloudGlossarySyncing=false;
                TermFeedback=await SaveTermEditorAsync()?"云端所选已删除，本机副本保留。":"云端已删除，本机关联保存失败，请重新核对。";
                return;
            }
            var staged=TermDraft.Select(t=>t.Clone()).ToList();
            var bases=new Dictionary<string,CloudGlossaryEntry>(_workspace.SyncBasis);
            foreach(var e in choice.Entries)
            {
                var t=staged.FirstOrDefault(t=>t.SourceKind==GlossarySource.User && (t.CloudId==e.Id || t.LocalId==e.Id));
                if(t==null)
                {
                    var matches=staged.Where(t=>t.SourceKind==GlossarySource.User && t.CloudId==null && t.SourceLang==e.SourceLang && t.TargetLang==e.TargetLang && t.Source.Trim().Equals(e.Source.Trim(),StringComparison.OrdinalIgnoreCase) && t.Target.Trim()==e.Target.Trim()).ToList();
                    if(matches.Count>1)throw new InvalidOperationException("本机存在多个候选，未下载；请先整理重复词条。");
                    t=matches.SingleOrDefault();
                }
                if(t!=null && !CloudGlossaryMerge.Equal(ToCloudTerm(t),e))
                {
                    bases.TryGetValue(e.Id!,out var basis);
                    if(basis==null || !CloudGlossaryMerge.Equal(ToCloudTerm(t),basis))
                    {
                        var answer=Views.PromptDialog.Show($"「{e.Source}」本机：{t.Target}；云端：{e.Target}。\n采用本机会保留待上传修改；取消则整个下载不落盘。","下载冲突",MessageBoxButton.YesNoCancel,confirmText:"采用云端",rejectText:"保留本机",cancelText:"取消批次");
                        if(answer==MessageBoxResult.Cancel)return;
                        if(answer==MessageBoxResult.No){t.CloudId=e.Id;bases[e.Id!]=e;continue;}
                    }
                }
                if(t==null) {t=new GlossaryEntry {SourceKind=GlossarySource.User};staged.Add(t);}
                ApplyCloudTerm(t,e);bases[e.Id!]=e;
            }
            var oldBasis=_workspace.SyncBasis; _workspace.SyncBasis=bases;
            if(!await CommitWorkspaceChangeAsync(()=> {TermDraft.Clear();foreach(var t in staged)TermDraft.Add(t);}))_workspace.SyncBasis=oldBasis;
            else TermFeedback="所选云端术语已合并到本机；其他本机词条保留。待确认方向词条需指定方向后才生效。";
        }
        catch(Exception ex) {Log.Warning(ex, "云端术语工作区操作未确认，本机数据保留");if(context==CloudGlossaryContext)TermFeedback="云端操作未确认，本机数据保留。"+ex.Message;}
        finally {IsCloudGlossarySyncing=false;RefreshTermView();}
    }
}
