using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DwgTranslator.Core.Api;
namespace DwgTranslator.App.Views;
public sealed record GlossaryCloudChoice(bool Delete, IReadOnlyList<CloudGlossaryEntry> Entries);
public sealed class GlossaryCloudWindow : Window
{
    public static GlossaryCloudChoice? Choose(IReadOnlyList<CloudGlossaryEntry> entries,string account,DateTime checkedAt)
    {
        GlossaryCloudChoice? result=null;
        // §弹窗排版：高度随内容收敛（此前固定 640，只有一行数据时表格与按钮之间留下约 300 DIP 空白）。
        // 非客户区（标题栏+边框）≈40、固定 chrome≈166、表头≈34、每行≈40。
        var height=Math.Clamp(40+166+34+entries.Count*40,300,640);
        var w=new GlossaryCloudWindow {Title="云端术语管理",Width=1050,Height=height,MinWidth=600,MinHeight=260,Owner=Application.Current.MainWindow,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        Controls.DialogShell.Constrain(w);
        var panel=new DockPanel {Margin=new Thickness(20)}; w.Content=panel;
        var top=new StackPanel(); var toolbar = new Controls.AdaptiveToolbar { Content = top }; DockPanel.SetDock(toolbar,Dock.Top);panel.Children.Add(toolbar);
        top.Children.Add(new TextBlock {TextWrapping=TextWrapping.Wrap,Text=$"账号：{account}  ·  云端 {entries.Count} 条  ·  核对：{checkedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}",Margin=new Thickness(0,0,0,12)});
        var filters=new WrapPanel();top.Children.Add(filters);
        var search=new TextBox {Width=240,Height=36,VerticalContentAlignment=VerticalAlignment.Center,ToolTip="搜索原文或译文",Margin=new Thickness(0,0,8,10)};filters.Children.Add(search);
        var category=new ComboBox {Width=160,Height=36,VerticalContentAlignment=VerticalAlignment.Center,ItemsSource=new[]{"全部分类"}.Concat(entries.Select(e=>string.IsNullOrWhiteSpace(e.Category)?"默认分类":e.Category).Distinct()).ToList(),SelectedIndex=0,Margin=new Thickness(0,0,0,10)};filters.Children.Add(category);
        string Direction(CloudGlossaryEntry e)=>e.DirectionPending?"待确认方向":e.SourceLang+" → "+e.TargetLang;
        var direction=new ComboBox {Width=160,Height=36,VerticalContentAlignment=VerticalAlignment.Center,ItemsSource=new[]{"全部方向"}.Concat(entries.Select(Direction).Distinct()).ToList(),SelectedIndex=0,Margin=new Thickness(8,0,0,10)};filters.Children.Add(direction);
        var view=new ListCollectionView(entries.ToList());
        var grid=new DataGrid {ItemsSource=view,AutoGenerateColumns=false,IsReadOnly=true,SelectionMode=DataGridSelectionMode.Extended,SelectionUnit=DataGridSelectionUnit.FullRow,CanUserAddRows=false};
        foreach(var pair in new[]{("原文","Source"),("译文","Target"),("分类","Category"),("源语言","SourceLang"),("目标语言","TargetLang")})grid.Columns.Add(new DataGridTextColumn {Header=pair.Item1,Binding=new Binding(pair.Item2),MinWidth=100,Width=new DataGridLength(1,DataGridLengthUnitType.Star)});
        foreach (var pair in new[] { ("方向待确认", "DirectionPending"), ("启用", "Enabled") })
            grid.Columns.Add(new DataGridCheckBoxColumn { Header=pair.Item1, Binding=new Binding(pair.Item2), Width=100 });
        void Filter(){grid.UnselectAll();view.Filter=o=>o is CloudGlossaryEntry e && (e.Source+" "+e.Target).Contains(search.Text,StringComparison.OrdinalIgnoreCase) && (category.SelectedIndex==0 || (string.IsNullOrWhiteSpace(e.Category)?"默认分类":e.Category)==category.SelectedItem?.ToString()) && (direction.SelectedIndex==0 || Direction(e)==direction.SelectedItem?.ToString());}
        search.TextChanged+=(_,_)=>Filter();category.SelectionChanged+=(_,_)=>Filter();direction.SelectionChanged+=(_,_)=>Filter();
        var bottom=new WrapPanel {Margin=new Thickness(0,12,0,0)};DockPanel.SetDock(bottom,Dock.Bottom);panel.Children.Add(bottom);
        var all=new Button {Content="选择当前结果",Margin=new Thickness(0,0,8,0)};all.SetResourceReference(FrameworkElement.StyleProperty,"Button.Secondary");all.Click+=(_,_)=>grid.SelectAll();bottom.Children.Add(all);
        foreach(var delete in new[]{false,true}){var b=new Button {Content=delete?"删除云端所选":"下载所选到本机",IsEnabled=false,Margin=new Thickness(0,0,8,0)};b.SetResourceReference(FrameworkElement.StyleProperty,delete?"Button.SecondaryDanger":"Button.Primary");grid.SelectionChanged+=(_,_)=>b.IsEnabled=grid.SelectedItems.Count>0;b.Click+=(_,_)=>{if(grid.SelectedItems.Count==0)return;result=new(delete,grid.SelectedItems.Cast<CloudGlossaryEntry>().ToList());w.DialogResult=true;};bottom.Children.Add(b);}
        panel.Children.Add(grid);w.ShowDialog();return result;
    }
}
