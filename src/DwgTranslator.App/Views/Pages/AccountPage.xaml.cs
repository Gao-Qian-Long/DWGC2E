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
        IsVisibleChanged += (_, _) => { if (IsVisible) Dispatcher.BeginInvoke(new Action(() => AccountInput.Focus())); else PasswordInput.Clear(); };
    }
    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var password = PasswordInput.Password;
        PasswordInput.Clear();
        await vm.SubmitLoginAsync(password);
    }
    private void Password_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; Login_Click(sender, e); } }
}