using DwgTranslator.Core.Models;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Service for managing application licensing, activation, and trial usage.
/// </summary>
public interface ILicenseService
{
    /// <summary>Current license information.</summary>
    LicenseInfo CurrentLicense { get; }

    /// <summary>True if the current license allows the operation to proceed.</summary>
    bool CanExecuteOperation();

    /// <summary>Consumes one trial use. Returns false if no uses remain.</summary>
    bool ConsumeTrialUse();

    /// <summary>Activates the application with the given activation code.</summary>
    /// <returns>Activation result message.</returns>
    (bool Success, string Message) Activate(string activationCode);

    /// <summary>Generates a machine-bound activation request string.</summary>
    string GenerateActivationRequest();

    /// <summary>Saves current license state to encrypted storage.</summary>
    void SaveLicense();

    /// <summary>Loads license from encrypted storage or initializes trial.</summary>
    void LoadLicense();
}
