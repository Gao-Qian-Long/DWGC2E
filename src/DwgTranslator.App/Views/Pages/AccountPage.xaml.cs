using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DwgTranslator.App.ViewModels;
namespace DwgTranslator.App.Views.Pages;
public partial class AccountPage : UserControl
{
    public AccountPage()
    {
        InitializeComponent();
        Loaded += (_, _) => { var w = Window.GetWindow(this); if(w != null) { w.Activated -= Owner_Activated; w.Activated += Owner_Activated; } };
        Unloaded += (_, _) => { var w = Window.GetWindow(this); if(w != null) w.Activated -= Owner_Activated; };
        IsVisibleChanged += async (_, _) => { if (IsVisible && DataContext is MainViewModel vm) await vm.RefreshMembershipOnActivationAsync(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) Dispatcher.BeginInvoke(new Action(() => AccountInput.Focus())); else PasswordInput.Clear(); };
    }
    private async void Owner_Activated(object? sender, EventArgs e) { if(IsVisible && DataContext is MainViewModel vm) await vm.RefreshMembershipOnActivationAsync(); }
    private void AccountMenu_Click(object sender, RoutedEventArgs e) { if(sender is Button b && b.ContextMenu is {} menu) { menu.DataContext=DataContext; menu.PlacementTarget=b; menu.IsOpen=true; } }
    private bool _submitting;
    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (_submitting || DataContext is not MainViewModel vm || vm.IsLoggingIn || vm.IsAccountRefreshing) return;
        AccountInput.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        var password = PasswordInput.Password;
        _submitting = true;
        try { await vm.SubmitLoginAsync(password); if(vm.IsAccountLoggedIn) PasswordInput.Clear(); }
        finally { _submitting = false; }
    }
    private void Password_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; Login_Click(sender, e); } }
}