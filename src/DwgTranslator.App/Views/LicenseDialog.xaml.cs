using DwgTranslator.Core.Models;
using DwgTranslator.Core.Resources;
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
            LicenseType.Trial => Strings.Get("LicenseTrialUses", license.TrialUsesRemaining),
            LicenseType.Perpetual => Strings.Get("LicenseNaPerpetual"),
            LicenseType.Subscription => license.ExpiryDate.HasValue
                ? Strings.Get("LicenseNaExpiry", license.ExpiryDate.Value.ToString("yyyy-MM-dd"))
                : Strings.Get("LicenseNa"),
            _ => Strings.Get("LicenseNa")
        };

        LicenseTypeText.Text = license.Type switch
        {
            LicenseType.Trial => Strings.Get("LicenseTrial"),
            LicenseType.Perpetual => Strings.Get("LicensePerpetual"),
            LicenseType.Subscription => Strings.Get("LicenseSubscription", $"{license.ExpiryDate:yyyy-MM-dd}"),
            _ => Strings.Get("LicenseNotActivated")
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
            ActivationCodeBox.Text = Strings.Get("LicenseAlreadyActivated");
            ActivationCodeBox.Foreground = System.Windows.Media.Brushes.Green;
        }
    }

    private void Activate_Click(object sender, RoutedEventArgs e)
    {
        var code = ActivationCodeBox.Text.Trim();
        if (string.IsNullOrEmpty(code))
        {
            ResultText.Text = Strings.Get("LicenseEnterCodePrompt");
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
        ResultText.Text = Strings.Get("LicenseMachineIdCopied");
        ResultText.Foreground = System.Windows.Media.Brushes.Green;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        try { DialogResult = true; } catch { }
        Close();
    }
}
