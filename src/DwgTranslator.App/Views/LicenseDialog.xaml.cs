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
        StatusText.Foreground = license.IsValid
            ? System.Windows.Media.Brushes.Green
            : System.Windows.Media.Brushes.OrangeRed;

        MachineIdBox.Text = LicenseService.GetMachineId();

        TrialText.Text = license.Type switch
        {
            LicenseType.Trial => $"{license.TrialUsesRemaining} 次",
            LicenseType.Perpetual => "N/A (已永久授权)",
            LicenseType.Subscription => license.ExpiryDate.HasValue
                ? $"到期: {license.ExpiryDate.Value:yyyy-MM-dd}"
                : "N/A",
            _ => "N/A"
        };

        LicenseTypeText.Text = license.Type switch
        {
            LicenseType.Trial => "体验版",
            LicenseType.Perpetual => "买断制 (永久)",
            LicenseType.Subscription => $"订阅制 (到期: {license.ExpiryDate:yyyy-MM-dd})",
            _ => "未激活"
        };

        LicenseTypeText.Foreground = license.Type switch
        {
            LicenseType.Perpetual => System.Windows.Media.Brushes.Green,
            LicenseType.Subscription => new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1976D2")),
            _ => new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#78909C"))
        };

        // Disable activation input if already permanently licensed
        if (license.Type == LicenseType.Perpetual)
        {
            ActivationCodeBox.IsEnabled = false;
            ActivateButton.IsEnabled = false;
            ActivationCodeBox.Text = "已永久授权 - 感谢您的支持!";
            ActivationCodeBox.Foreground = System.Windows.Media.Brushes.Green;
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
        try { DialogResult = true; } catch { }
        Close();
    }
}
