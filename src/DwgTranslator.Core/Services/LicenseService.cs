using DwgTranslator.Core.Models;
namespace DwgTranslator.Core.Services;

/// <summary>Compatibility adapter. Offline licenses no longer grant or restrict cloud access.</summary>
public sealed class LicenseService : ILicenseService
{
    public static string GetMachineId() => MachineIdentifier.GetLicenseBindingId();
    public LicenseService(string appDataDir) { }
    public LicenseInfo CurrentLicense { get; } = new();
    public bool CanExecuteOperation() => false;
    public bool ConsumeTrialUse() => false;
    public (bool Success,string Message) Activate(string activationCode) => (false,"离线授权已停用，请登录云端账号。");
    public string GenerateActivationRequest() => "请登录云端账号";
    public void SaveLicense() { }
    public void LoadLicense() { }
}
