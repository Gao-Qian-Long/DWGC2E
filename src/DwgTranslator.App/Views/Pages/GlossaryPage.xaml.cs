using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DwgTranslator.App.ViewModels;
using DwgTranslator.Core.Models;
namespace DwgTranslator.App.Views.Pages;
public partial class GlossaryPage : UserControl
{
    private MainViewModel? Vm => DataContext as MainViewModel;
    private bool _updating;
    public GlossaryPage()
    {
        InitializeComponent();
        TermEditorDrawer.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => Vm?.NotifyTermDraftEdited()));
        TermEditorDrawer.AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => Vm?.NotifyTermDraftEdited()));
        TermEditorDrawer.AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => Vm?.NotifyTermDraftEdited()));
    }
    private List<GlossaryEntry> Selection() => TermList.SelectedItems.Cast<GlossaryEntry>().ToList();
    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (NormalToolbar == null) return;
        var count = TermList.SelectedItems.Count;
        NormalToolbar.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectionToolbar.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectionCount.Text = $"已选择 {count} 项";
        DeleteSelectionButton.IsEnabled = Selection().Any(t => t.SourceKind == GlossarySource.User);
        SelectAll.IsChecked = count > 0 && count == TermList.Items.Count;
    }
    private void SelectAll_Click(object s, RoutedEventArgs e) { if (SelectAll.IsChecked == true) TermList.SelectAll(); else TermList.UnselectAll(); }
    private void ClearSelection_Click(object s, RoutedEventArgs e) => TermList.UnselectAll();
    private void Status_Click(object s, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var state = ((Button)s).Tag?.ToString() ?? "All";
        Vm.TermStatusFilter = state;
        TermList.UnselectAll();
    }
    private void ClearFilters_Click(object s, RoutedEventArgs e) { if (Vm is { } vm) { vm.TermSearch = ""; vm.TermSourceFilter = 0; vm.TermCategoryFilter = ""; vm.TermConflictsOnly = false; vm.TermStatusFilter = "All"; vm.TermScope = 0; } }
    private async void Toggle_Click(object s, RoutedEventArgs e)
    {
        if (Vm is { } vm && ((FrameworkElement)s).DataContext is GlossaryEntry term)
        { await vm.SetWorkspaceEnabledAsync(new[] { term }, !term.Enabled); TermList.Items.Refresh(); e.Handled = true; }
    }
    private async void BatchEnabled_Click(object s, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var terms = Selection(); var enabled = ((Button)s).Tag?.ToString() == "True";
        if (await vm.SetWorkspaceEnabledAsync(terms, enabled)) vm.TermFeedback = $"已{(enabled ? "启用" : "停用")} {terms.Count} 条 · 本机已保存";
        TermList.Items.Refresh();
    }
    private async void BatchCategory_Click(object s, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var terms = Selection().Where(t => t.SourceKind == GlossarySource.User).ToList();
        if (terms.Count == 0) { vm.TermFeedback = "只有用户术语可以修改分类。"; return; }
        var category = BatchCategory.Text.Trim();
        if (await vm.CommitWorkspaceChangeAsync(() => terms.ForEach(t => t.Category = category))) vm.TermFeedback = $"已修改 {terms.Count} 条用户术语的分类，系统及企业术语未修改。";
        TermList.Items.Refresh();
    }
    private void Category_Changed(object s, SelectionChangedEventArgs e)
    {
        if (_updating || Vm is not { } vm || s is not ComboBox box || !box.IsKeyboardFocusWithin || box.DataContext is not GlossaryEntry term || box.SelectedItem is not string value || value == term.Category) return;
        _updating = true;
        Dispatcher.BeginInvoke(new Action(async () => { try { await vm.EditWorkspaceTextAsync(term, "Category", value); TermList.Items.Refresh(); } finally { _updating = false; } }));
    }
    private void Delete_Click(object s, RoutedEventArgs e) => DeleteSelected();
    private async void DeleteSelected()
    {
        if (Vm is not { } vm) return;
        var terms = Selection().Where(t => t.SourceKind == GlossarySource.User).ToList();
        if (terms.Count == 0) { vm.TermFeedback = "系统及企业术语不可删除，请停用。"; return; }
        if (PromptDialog.Show($"删除选中的 {terms.Count} 条自定义术语？系统及企业术语将保留。", "删除术语", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await vm.CommitWorkspaceChangeAsync(() => terms.ForEach(t => vm.TermDraft.Remove(t)));
    }
    private void Export_Click(object s, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var terms = Selection(); if (terms.Count == 0) terms = vm.TermView.Cast<GlossaryEntry>().ToList();
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "JSON 术语库|*.json", FileName = "glossary.json" };
        if (dialog.ShowDialog() != true) return;
        try { File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(terms, AppConfigJson.WriteOptions)); vm.TermFeedback = $"已导出 {terms.Count} 条术语。"; } catch (IOException) { vm.TermFeedback = "导出失败，请检查文件权限。"; } catch (UnauthorizedAccessException) { vm.TermFeedback = "导出失败，请检查文件权限。"; }
    }
    private void Edit_Click(object s, RoutedEventArgs e) { if (((FrameworkElement)s).DataContext is GlossaryEntry t) Vm?.OpenTermDrawer(t); }
    private void Copy_Click(object s, RoutedEventArgs e) { if (((FrameworkElement)s).DataContext is GlossaryEntry t) Vm?.CopyTermToDrawer(t); }
    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not GlossaryEntry term || Vm is not { } vm) return;
        var menu = new ContextMenu { PlacementTarget = button };
        var details = new MenuItem { Header = "查看完整属性 / 冲突" };
        details.Click += (_, _) => vm.OpenTermDrawer(term);
        var copy = new MenuItem { Header = "复制为用户术语" };
        copy.Click += (_, _) => vm.CopyTermToDrawer(term);
        var toggle = new MenuItem { Header = term.Enabled ? "停用" : "启用" };
        toggle.Click += async (_, _) => await vm.SetWorkspaceEnabledAsync(new[] { term }, !term.Enabled);
        menu.Items.Add(details); menu.Items.Add(copy); menu.Items.Add(toggle);
        if (term.SourceKind == GlossarySource.User)
        {
            var delete = new MenuItem { Header = "删除此用户术语" };
            delete.Click += (_, _) => { TermList.SelectedItem = term; TermList.UnselectAll(); TermList.SelectedItem = term; DeleteSelected(); };
            menu.Items.Add(new Separator()); menu.Items.Add(delete);
        }
        button.ContextMenu = menu; menu.IsOpen = true; e.Handled = true;
    }
    private void CancelDrawer_Click(object s, RoutedEventArgs e) => Vm?.CancelTermDrawer();
    private void BeginningEdit(object s, DataGridBeginningEditEventArgs e) { if (Vm?.CanEditWorkspace != true || ((GlossaryEntry)e.Row.Item).SourceKind != GlossarySource.User) e.Cancel = true; }
    private void CellEditEnding(object s, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.EditingElement is not TextBox input || Vm is not { } vm) return;
        var term = (GlossaryEntry)e.Row.Item; var index = vm.TermDraft.IndexOf(term); var column = e.Column; var value = input.Text;
        var field = column.Header?.ToString() == "原文" ? "Source" : "Target";
        // WorkspaceTextColumn supplies an unbound editor so WPF cannot bypass the transaction snapshot.
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            TermList.CommitEdit(DataGridEditingUnit.Row, true);
            if (await vm.EditWorkspaceTextAsync(term, field, value)) return;
            if (index < 0 || index >= vm.TermDraft.Count) return;
            var restored = vm.TermDraft[index]; TermList.SelectedItem = restored; TermList.CurrentCell = new DataGridCellInfo(restored, column); TermList.BeginEdit();
            _ = Dispatcher.BeginInvoke(new Action(() => { if (Keyboard.FocusedElement is TextBox editor) { editor.Text = value; editor.SelectAll(); } }));
        }), DispatcherPriority.Background);
    }
    private async void Workspace_KeyDown(object s, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (ctrl && e.Key == Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.N) { vm.AddTermCommand.Execute(null); e.Handled = true; return; }
        if (e.Key == Key.Escape && vm.IsTermDrawerOpen) { vm.CancelTermDrawer(); e.Handled = true; return; }
        if (TermList.IsKeyboardFocusWithin && vm.TermView is System.ComponentModel.IEditableCollectionView editing && editing.IsEditingItem)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                TermList.CancelEdit(DataGridEditingUnit.Cell); TermList.CancelEdit(DataGridEditingUnit.Row);
                vm.RefreshTermView(); return;
            }
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                TermList.CommitEdit(DataGridEditingUnit.Cell, true); TermList.CommitEdit(DataGridEditingUnit.Row, true); return;
            }
        }
        if (e.OriginalSource is TextBox || e.OriginalSource is ComboBox) return;
        if (!TermList.IsKeyboardFocusWithin || vm.IsTermDrawerOpen) return;
        if (ctrl && e.Key == Key.A) { TermList.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.Space && vm.SelectedTerm is { } term) { e.Handled = true; await vm.SetWorkspaceEnabledAsync(new[] { term }, !term.Enabled); TermList.Items.Refresh(); }
        else if (e.Key == Key.Enter && vm.SelectedTerm is { } current) { vm.OpenTermDrawer(current); e.Handled = true; }
        else if (e.Key == Key.Delete) { DeleteSelected(); e.Handled = true; }
        else if (e.Key == Key.Escape) { TermList.UnselectAll(); e.Handled = true; }
    }
    private void TermList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element != null && element is not DataGridCell) element = VisualTreeHelper.GetParent(element);
        if (element is DataGridCell cell && cell.Column is DataGridTextColumn && cell.Column.IsReadOnly && cell.DataContext is GlossaryEntry term)
        { Vm?.OpenTermDrawer(term); e.Handled = true; }
    }
    private void TermRow_Loading(object s, DataGridRowEventArgs e) { e.Row.Tag = e.Row.Item is GlossaryEntry t && Vm?.HasTermConflict(t) == true; }
    private void DetailColumns_Changed(object s, RoutedEventArgs e)
    {
        if (TermList == null) return;
        foreach (var c in TermList.Columns) if (c.Header is "文件夹" or "方向" or "最近命中") c.Visibility = DetailColumns.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }
}
