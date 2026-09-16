using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DwgTranslator.Core.Api;
using DwgTranslator.App.Views;
namespace UiSmoke;
public sealed partial class SmokeApp
{
 private async Task VerifyBillingCatalog(MainWindow owner)
 {
  var fake=new FakeBilling{UserId="catalog-ui-isolated",FailPlans=true,Catalog=new BillingPlans{PaymentsEnabled=true,Plans=[
   new(){Id="free",Name="Free",PriceCents=0,DurationDays=30},
   new(){Id="pro",Name="Pro",PriceCents=8,DurationDays=30},
   new(){Id="max",Name="Max",PriceCents=3,DurationDays=30},
   new(){Id="go",Name="Go",PriceCents=70,DurationDays=30},
   new(){Id="disabled",Name="Disabled",PriceCents=99,DurationDays=30,Enabled=0}]}};
  BillingWindow Create()=>new(fake,"catalog@example.test",()=>true,_=>{}){Owner=owner,ShowActivated=false,ShowInTaskbar=false};
  async Task Idle(BillingWindow w){await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);await WaitUntil(()=>(bool)typeof(BillingWindow).GetField("_busy",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(w)! == false,"catalog request finishes");}
  var w=Create();w.Show();await Idle(w);
  Check(((TextBlock)w.FindName("PurchaseAvailability")).Text.Contains("加载失败"),"failed catalog stops showing checking service");
  Check(!((Button)w.FindName("Buy")).IsEnabled,"failed catalog cannot create purchase");
  Check(!((TextBlock)w.FindName("Message")).Text.Contains("操作未确认"),"read-only catalog error is not presented as payment uncertainty");
  fake.FailPlans=false;
  typeof(BillingWindow).GetMethod("Refresh_Click",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(w,new object[]{w,new RoutedEventArgs()});await Idle(w);
  var plans=(ComboBox)w.FindName("Plans");
  Check(plans.Items.Cast<BillingPlan>().Select(p=>p.Id).SequenceEqual(new[]{"pro","max","go"}),"free and disabled tiers do not block or appear among paid choices");
  Check(((BillingPlan)plans.SelectedItem).Label.Contains("0.08"),"catalog preserves server cents without price invention");
  Check(((Button)w.FindName("Buy")).IsEnabled,"catalog refresh recovers purchase selection");
  Capture(w,"billing-catalog-recovered");w.Close();
  fake.Catalog.PaymentsEnabled=false;w=Create();w.Show();await Idle(w);
  Check(!((Button)w.FindName("Buy")).IsEnabled && ((TextBlock)w.FindName("PurchaseAvailability")).Text.Contains("暂未开放"),"server purchase switch still enforced for mixed catalog");w.Close();
  fake.Catalog.PaymentsEnabled=true;fake.Catalog.Plans.RemoveAll(p=>p.Id!="free");w=Create();w.Show();await Idle(w);
  Check(((ComboBox)w.FindName("Plans")).Visibility==Visibility.Collapsed && !((Button)w.FindName("Buy")).IsEnabled,"free-only catalog has no checkout choice");
  Check(((TextBlock)w.FindName("PurchaseAvailability")).Text.Contains("暂无可购买套餐"),"free-only catalog has explicit empty state");
  Check(fake.Creates==0,"catalog loading retries and empty states never create orders");w.Close();
 }
}
