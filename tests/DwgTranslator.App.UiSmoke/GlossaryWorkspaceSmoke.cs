using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Input;
using DwgTranslator.App.ViewModels;
using DwgTranslator.App.Views.Pages;
using DwgTranslator.Core.Models;
namespace UiSmoke;
public sealed partial class SmokeApp
{
    private async Task VerifyGlossaryWorkspace(Window window, MainViewModel vm)
    {
        var original = vm.TermDraft.Select(t => t.Clone()).ToList();
        vm.TermDraft.Clear();
        vm.TermDraft.Add(new GlossaryEntry { Source="workspace-user", Target="User", SourceKind=GlossarySource.User, Category="mechanical" });
        vm.TermDraft.Add(new GlossaryEntry { Source="workspace-system", Target="System", SourceKind=GlossarySource.System });
        Check(vm.SaveTermEditor(), "workspace fixture persisted");
        try
        {
            var user=vm.TermDraft[0]; var system=vm.TermDraft[1];
            vm.SelectedTerm=user;
            Check(!vm.IsTermDrawerOpen, "row selection never opens drawer");
            Check(await vm.SetWorkspaceEnabledAsync(new[] { user }, false) && !user.Enabled && !vm.HasUnsavedTerms, "one-step toggle auto saves locally");
            Check(await vm.SetWorkspaceEnabledAsync(vm.TermDraft.ToList(), true) && vm.TermDraft.All(t=>t.Enabled), "batch enable persists atomically");
            Check(!await vm.EditWorkspaceTextAsync(system,"Source","forbidden") && system.Source=="workspace-system", "system content cannot be edited");
            Check(await vm.EditWorkspaceTextAsync(user,"Target","Inline") && !vm.HasUnsavedTerms, "inline Enter-equivalent auto saves");
            Check(!await vm.EditWorkspaceTextAsync(user,"Target","") && vm.TermDraft[0].Target=="Inline" && !vm.HasUnsavedTerms, "invalid inline change rolls back");
            var directory=(string)typeof(MainViewModel).GetProperty("AccountDataDirectory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(vm)!;
            var path=System.IO.Path.Combine(directory,"glossaries","workspace-v2.json");
            var bytes=System.IO.File.ReadAllBytes(path);
            using(var locked=new System.IO.FileStream(path,System.IO.FileMode.Open,System.IO.FileAccess.Read,System.IO.FileShare.Read))
            {
                var save=vm.SetWorkspaceEnabledAsync(vm.TermDraft.ToList(),false);
                Check(vm.IsWorkspaceSaving && !vm.CanEditWorkspace && !vm.ConfirmLeaveGlossary(),"pending save blocks editing/navigation");
                Check(!await save,"locked local file rejects batch");
            }
            Check(vm.TermDraft.All(t=>t.Enabled) && !vm.HasUnsavedTerms,"failed batch restores all entries");
            Check(bytes.SequenceEqual(System.IO.File.ReadAllBytes(path)),"failed batch preserves file bytes");
            Check(!vm.IsWorkspaceSaving && !vm.IsGlossaryLoading,"failed save releases guards");
            user=vm.TermDraft[0];
            vm.OpenTermDrawer(user); user.Target="cancelled"; vm.CancelTermDrawer();
            Check(vm.TermDraft[0].Target=="Inline" && !vm.IsTermDrawerOpen && !vm.HasUnsavedTerms, "drawer cancellation restores prior data");
            vm.CopyTermToDrawer(vm.TermDraft[1]);
            Check(vm.SelectedTerm!.SourceKind==GlossarySource.User && vm.SelectedTerm.CloudId==null && !vm.SelectedTerm.Enabled, "system copy becomes local user draft");
            vm.CancelTermDrawer();
            vm.TermStatusFilter="Disabled"; Check(vm.TermView.IsEmpty, "disabled filter excludes enabled rows");
            vm.TermStatusFilter="All"; vm.TermSourceFilter=1; Check(vm.TermView.Cast<GlossaryEntry>().Single().SourceKind==GlossarySource.System,"source filter");
            vm.TermSourceFilter=0; vm.TermCategoryFilter="mechanical"; Check(vm.TermView.Cast<GlossaryEntry>().Count()==1,"category filter");
            vm.TermCategoryFilter=""; vm.TermSearch="workspace-user";
            await Task.Delay(400); Check(vm.TermView.Cast<GlossaryEntry>().Count()==1,"debounced search");
            vm.TermSearch=""; vm.RefreshTermView(); vm.SelectedTerm=null;
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            var page=FindVisual<GlossaryPage>(window); var table=(DataGrid)page.FindName("TermList");
            table.UnselectAll(); table.UpdateLayout();
            var toolbar=(FrameworkElement)page.FindName("SelectionToolbar");
            // 用户要求（2026-09-19 截图反馈）：批量工具条不再随勾选出现/消失，改为始终占位、无选中时置灰。
            // 这里曾经断言 Collapsed，那是旧行为；该断言随需求一起更新，不是为了让冒烟变绿而放宽。
            Check(toolbar.Visibility==Visibility.Visible && !toolbar.IsEnabled,"unselected toolbar stays visible but disabled");
            for(var selectionIndex=0;selectionIndex<2;selectionIndex++)
            {
                table.ScrollIntoView(vm.TermDraft[selectionIndex]); table.UpdateLayout();
                var row=(DataGridRow)table.ItemContainerGenerator.ContainerFromItem(vm.TermDraft[selectionIndex]);
                var checkbox=FindVisual<CheckBox>(row);
                checkbox.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=UIElement.PreviewMouseLeftButtonDownEvent});
            }
            Check(table.SelectedItems.Count==2,"checkbox clicks accumulate arbitrary selection without Ctrl");
            table.UnselectAll();
            Check(table.EnableRowVirtualization && table.EnableColumnVirtualization,"table virtualization enabled");
            table.SelectedItem=vm.TermDraft[0]; table.CurrentCell=new DataGridCellInfo(vm.TermDraft[0],table.Columns[2]);
            table.ScrollIntoView(vm.TermDraft[0]); table.Focus(); table.UpdateLayout();
            Check(table.BeginEdit(),"real table enters inline editor");
            table.UpdateLayout();
            var editor=FindVisual<TextBox>(table);
            Check(editor!=null,"inline textbox is rendered");
            editor!.Text="Keyboard saved";
            table.CommitEdit(DataGridEditingUnit.Cell,true); table.CommitEdit(DataGridEditingUnit.Row,true);
            await Task.Delay(500);
            Check(vm.TermDraft[0].Target=="Keyboard saved" && !vm.HasUnsavedTerms,"DataGrid commit saves via asynchronous transaction");
            table.UnselectAll();
            // Exercise the actual controls, not only ViewModel methods.
            table.SelectedItem=vm.TermDraft[0]; table.CurrentCell=new DataGridCellInfo(vm.TermDraft[0],table.Columns[2]);
            table.Focus(); table.UpdateLayout();
            void Press(Key key)
            {
                var target=Keyboard.FocusedElement as UIElement ?? table;
                var args=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(table),Environment.TickCount,key) { RoutedEvent=Keyboard.PreviewKeyDownEvent };
                target.RaiseEvent(args);
                if(!args.Handled) { args.RoutedEvent=Keyboard.KeyDownEvent; target.RaiseEvent(args); }
            }
            Press(Key.F2); table.UpdateLayout();
            // 焦点可能瞬时落在行内编辑器又弹回，必须在轮询里捕获引用，后续只用捕获到的对象。
            TextBox? inlineEditor = null;
            await WaitUntil(() => { inlineEditor = Keyboard.FocusedElement as TextBox; return inlineEditor != null; }, "F2 opens inline text editor");
            Check(inlineEditor != null, "F2 focuses inline text editor");
            inlineEditor!.Text = "Cancelled with Escape";
            Press(Key.Escape); await Task.Delay(100);
            Check(vm.TermDraft[0].Target=="Keyboard saved" && !vm.HasUnsavedTerms,"Escape cancels inline edit without saving");
            table.Focus(); Press(Key.Space);
            await WaitUntil(() => !vm.IsWorkspaceSaving && !vm.TermDraft[0].Enabled && !vm.HasUnsavedTerms, "keyboard Space transaction completes");
            Check(!vm.TermDraft[0].Enabled && !vm.HasUnsavedTerms,"Space toggles and persists current row");
            table.SelectedItem=vm.TermDraft[0]; table.Focus(); Press(Key.Enter);
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Input);
            Check(vm.IsTermDrawerOpen,"Enter opens drawer from focused row");
            var termDrawer=(FrameworkElement)page.FindName("TermEditorDrawer");
            var drawerCancel=(Button)page.FindName("TermDrawerCancelButton");
            Check(termDrawer.IsKeyboardFocusWithin && ReferenceEquals(Keyboard.FocusedElement,drawerCancel),"term drawer moves focus to its cancel action");
            Press(Key.Escape); await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            Check(!vm.IsTermDrawerOpen,"Escape closes drawer");
            Check(table.IsKeyboardFocusWithin,"term drawer restores focus to the originating table");
            await vm.SetWorkspaceEnabledAsync(vm.TermDraft.ToList(),true);
            table.UpdateLayout();
            var toggle=FindVisuals<CheckBox>(table).First(c=>c.DataContext==vm.TermDraft[0] && System.Windows.Automation.AutomationProperties.GetName(c)=="启用术语");
            toggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            await WaitUntil(() => !vm.IsWorkspaceSaving && !vm.TermDraft[0].Enabled && !vm.HasUnsavedTerms, "rendered toggle transaction completes");
            Check(!vm.TermDraft[0].Enabled && !vm.HasUnsavedTerms,"rendered toggle click saves immediately");
            var category=FindVisuals<ComboBox>(table).First(c=>c.DataContext==vm.TermDraft[0]);
            category.Focus(); category.SelectedItem="默认分类";
            await WaitUntil(() => !vm.IsWorkspaceSaving && vm.TermDraft[0].Category=="默认分类" && !vm.HasUnsavedTerms, "rendered category transaction completes");
            Check(vm.TermDraft[0].Category=="默认分类" && !vm.HasUnsavedTerms,"rendered inline category saves immediately");
            var batch=(ComboBox)page.FindName("BatchCategory"); table.SelectAll(); batch.ApplyTemplate();
            Check(vm.ChangeTermCategory(null,"custom-category"),"create persistent category before assignment");
            batch.SelectedItem="custom-category";
            FindVisuals<Button>(page).Single(b=>Equals(b.Content,"修改分类")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => !vm.IsWorkspaceSaving && vm.TermDraft[0].Category=="custom-category" && vm.TermDraft[1].Category=="默认分类", "batch category transaction completes");
            Check(vm.TermDraft[0].Category=="custom-category" && vm.TermDraft[1].Category=="默认分类","typed batch category changes users only");
            table.SelectedItem=vm.TermDraft[0]; table.CurrentCell=new DataGridCellInfo(vm.TermDraft[0],table.Columns[2]); table.Focus(); Press(Key.F2);
            ((TextBox)Keyboard.FocusedElement).Text=""; Press(Key.Enter); await Task.Delay(300);
            Check(Keyboard.FocusedElement is TextBox retry && retry.Text=="","failed Enter save keeps attempted value in editor");
            Check(vm.TermDraft[0].Target=="Keyboard saved","failed Enter preserves committed model: "+vm.TermDraft[0].Target);
            Press(Key.Escape);
            table.UnselectAll();
            Check(vm.TermLocalStatus.Contains("已保存") && vm.TermCloudStatus.Contains("手动同步"),"local/cloud status channels remain distinct");
            vm.TermFeedback="本机已保存 · 云端需手动同步";
            await Task.Delay(4200);
            var width=window.Width; var height=window.Height;
            foreach(var size in new[] {1366d,1920d,2560d}) { window.Width=size; window.Height=900; await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle); Capture(window,"workspace-"+size); }
            window.Width=width; window.Height=height;
            table.SelectedItem=vm.TermDraft[1]; table.UpdateLayout();
            var more=FindVisuals<Button>(table).First(b=>b.DataContext==vm.TermDraft[1] && Equals(b.Content,"⋯"));
            Check(more.ActualHeight == 32 && table.RowHeight >= more.ActualHeight + 8,"row quick actions fit inside compact cell padding");
            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(more.ContextMenu.Items.OfType<MenuItem>().All(m=>!m.Header.ToString()!.Contains("删除")),"system row menu never offers deletion");
            more.ContextMenu.IsOpen=false;
            vm.OpenTermDrawer(vm.TermDraft[0]); await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            var sourceEditor=FindVisuals<TextBox>((FrameworkElement)page.FindName("TermEditorDrawer")).First(t=>t.Text==vm.TermDraft[0].Source);
            sourceEditor.Text="Drawer edited source";
            Check(vm.HasUnsavedTerms,"drawer input marks complex draft dirty");
            Capture(window,"workspace-drawer-verified");
            vm.FinishTermEditCommand.Execute(null);
            // FinishTermEditCommand is an async command now, so Execute returns before the save finishes.
            await WaitUntil(() => !vm.IsTermDrawerOpen && !vm.HasUnsavedTerms && vm.TermDraft[0].Source == "Drawer edited source", "drawer save commits and returns to list");
            Check(!vm.IsTermDrawerOpen && !vm.HasUnsavedTerms && vm.TermDraft[0].Source=="Drawer edited source","drawer save commits and returns to list");
            table.SelectAll();
            await ConfirmCloudDialog(async ()=>
            {
                FindVisuals<Button>(page).Single(b=>Equals(b.Content,"删除本机")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(250);
            });
            await WaitUntil(() => vm.TermDraft.Count==1 && vm.TermDraft[0].SourceKind==GlossarySource.System && !vm.HasUnsavedTerms, "mixed selection delete settles");
            Check(vm.TermDraft.Count==1 && vm.TermDraft[0].SourceKind==GlossarySource.System && !vm.HasUnsavedTerms,"mixed selection delete confirms once and preserves system term");
            vm.TermDraft.Add(new GlossaryEntry { Source=vm.TermDraft[0].Source,Target="User override",Category="冲突测试",SourceKind=GlossarySource.User,SourceLang="ZH",TargetLang="EN" });
            Check(vm.SaveTermEditor(),"cross-priority conflict preserves existing business rule");
            vm.TermDraft.Add(new GlossaryEntry {Source=vm.TermDraft.Last().Source,Target="Conflicting user",Category="冲突测试",SourceKind=GlossarySource.User,SourceLang="ZH",TargetLang="EN"});
            Check(vm.SaveTermEditor(),"same-priority conflicts are saved for explicit resolution");
            vm.TermStatusFilter="Conflict"; vm.TermSourceFilter=3; vm.TermCategoryFilter="冲突测试";
            Check(vm.TermView.Cast<GlossaryEntry>().Count()==2,"status/source/category filters compose");
            vm.TermSourceFilter=0; vm.TermCategoryFilter=""; table.UpdateLayout();
            var conflictAction=FindVisuals<Button>(table).First(b=>Equals(b.Content,"冲突"));
            conflictAction.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(vm.IsTermDrawerOpen && vm.SelectedTermConflicts.Count>=1,"conflict row badge opens drawer with alternate translation");
            vm.CancelTermDrawer(); vm.TermStatusFilter="All";
            // Exercise the existing supported limit without changing the business cap.
            vm.TermDraft.Clear();
            for(var i=0;i<1000;i++) vm.TermDraft.Add(new GlossaryEntry {Source=$"性能术语-{i:D4}",Target=$"Engineering term {i:D4}",Category=i%2==0?"机械":"电气",SourceKind=GlossarySource.User});
            vm.TermDraft[0].Target=string.Concat(Enumerable.Repeat("Long engineering translation / ",12));
            Check(vm.SaveTermEditor(),"1000-entry fixture persists within existing limit");
            var timing=System.Diagnostics.Stopwatch.StartNew();
            var saving=vm.SetWorkspaceEnabledAsync(vm.TermDraft.ToList(),false);
            Check(vm.IsWorkspaceSaving && !vm.ShowTermDraftActions,"optimistic batch does not expose unified dirty bar");
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Input);
            Check(await saving && vm.TermDraft.All(t=>!t.Enabled),"1000-entry batch saves in one transaction");
            Console.WriteLine($"WORKSPACE_BATCH_1000_MS={timing.ElapsedMilliseconds}");
            Check(timing.Elapsed<TimeSpan.FromSeconds(5),"1000-entry batch completes without prolonged UI stall");
            timing.Restart(); vm.TermSearch="性能术语-0999"; await Task.Delay(400);
            Check(vm.TermView.Cast<GlossaryEntry>().Count()==1,"1000-entry debounced search yields exact result");
            Console.WriteLine($"WORKSPACE_SEARCH_1000_MS={timing.ElapsedMilliseconds}");
            vm.TermSearch=""; vm.RefreshTermView(); table.UpdateLayout();
            Check(FindVisuals<DataGridRow>(table).Count()<100,"1000-entry grid realizes only viewport rows");
            table.ScrollIntoView(vm.TermDraft.Last()); table.UpdateLayout();
            Check(table.ItemContainerGenerator.ContainerFromItem(vm.TermDraft.Last()) is DataGridRow,"virtualized table scrolls to final entry");
            table.ScrollIntoView(vm.TermDraft.First()); table.UpdateLayout();
            var longCell=FindVisuals<TextBlock>(table).First(t=>t.Text==vm.TermDraft[0].Target);
            Check(longCell.TextTrimming==TextTrimming.CharacterEllipsis && Equals(longCell.ToolTip,longCell.Text),"long table text trims and exposes complete tooltip");
            Capture(window,"workspace-1000-entries");
            vm.TermSearch="no-term-matches-this-query"; await Task.Delay(400);
            Check(vm.TermView.IsEmpty,"unmatched search exposes empty state");
            Capture(window,"workspace-glossary-empty");
            vm.TermSearch=""; vm.RefreshTermView();
        }
        finally
        {
            vm.TermSearch=""; vm.TermStatusFilter="All"; vm.TermSourceFilter=0; vm.TermCategoryFilter="";
            if(vm.IsTermDrawerOpen) vm.CancelTermDrawer();
            await WaitUntil(() => !vm.IsWorkspaceSaving && !vm.IsGlossaryLoading && !vm.IsCloudGlossarySyncing,
                "glossary workspace operations settle before fixture restore");
            vm.TermDraft.Clear(); foreach(var t in original) vm.TermDraft.Add(t);
            var restored = vm.SaveTermEditor();
            Check(restored, $"workspace fixture restored: {vm.TermFeedback}; invalid={vm.TermDraft.Count(t => !DwgTranslator.Core.Services.EffectiveGlossary.Valid(t))}; processing={vm.IsProcessing}");
            vm.SelectedTerm=null;
        }
    }
}
