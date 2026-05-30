using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using System.Windows;

namespace DwgTranslator.App.Views;

public partial class LicenseDialog : Window
{
    private readonly ILicenseService _licenseService;

    public LicenseDialog(ILicenseService licenseService)
    {
        InitializeComponent();
        _licenseService = licenseService;
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var license = _licenseService.CurrentLicense;
        StatusText.Text = license.GetDisplayStatus();
        StatusText.Foreground = license.IsValid ? System.Windows.Media.Brushes.Green : System.Windows.Media.Brushes.Orange;
        MachineIdBox.Text = LicenseService.GetMachineId();
        TrialText.Text = license.Type == LicenseType.Trial
            ? $"{license.TrialUsesRemaining} 次"
            : "N/A (已授权)";

        // Disable activation input if already permanently licensed
        if (license.Type == LicenseType.Perpetual)
        {
            ActivationCodeBox.IsEnabled = false;
            ActivateButton.IsEnabled = false;
            ActivationCodeBox.Text = "已永久授权";
        }
    }

    private void Activate_Click(object sender, RoutedEventArgs e)
    {
        var code = ActivationCodeBox.Text.Trim();
        if (string.IsNullOrEmpty(code))
        {
            ResultText.Text = "请输入激活码";
            ResultText.Foreground = System.Windows.Media.Brushes.Red;
            return;
        }

        var (success, message) = _licenseService.Activate(code);
        ResultText.Text = message;
        ResultText.Foreground = success ? System.Windows.Media.Brushes.Green : System.Windows.Media.Brushes.Red;

        if (success)
        {
            RefreshStatus();
            DialogResult = true;
        }
    }

    private void CopyMachineId_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(MachineIdBox.Text);
        ResultText.Text = "机器码已复制到剪贴板，请发送给客服获取激活码。";
        ResultText.Foreground = System.Windows.Media.Brushes.Green;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
