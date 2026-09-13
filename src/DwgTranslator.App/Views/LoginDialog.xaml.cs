using System.Windows;

namespace DwgTranslator.App.Views;

public partial class LoginDialog : Window
{
    private readonly Func<string, string, CancellationToken, Task<string?>> _login;
    private readonly CancellationTokenSource _cts = new();

    public LoginDialog(Func<string, string, CancellationToken, Task<string?>> login)
    {
        InitializeComponent();
        _login = login;
        Closed += (_, _) => { _cts.Cancel(); Password.Clear(); };
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Account.Text) || Password.Password.Length == 0)
        { Error.Text = "请输入账号和密码。"; return; }
        Submit.IsEnabled = false;
        Account.IsEnabled = false;
        Password.IsEnabled = false;
        Submit.Content = "正在登录…";
        Error.Text = "";
        try
        {
            var error = await _login(Account.Text.Trim(), Password.Password, _cts.Token);
            if (_cts.IsCancellationRequested) return;
            if (error == null) DialogResult = true;
            else Error.Text = error;
        }
        catch (OperationCanceledException) { }
        catch { if (!_cts.IsCancellationRequested) Error.Text = "登录失败，请检查服务连接后重试。"; }
        finally
        {
            Password.Clear();
            Submit.IsEnabled = true;
            Account.IsEnabled = true;
            Password.IsEnabled = true;
            Submit.Content = "登录";
        }
    }
}
