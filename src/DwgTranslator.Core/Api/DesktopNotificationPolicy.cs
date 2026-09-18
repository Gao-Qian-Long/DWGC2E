using System.Security.Cryptography;
using System.Text;

namespace DwgTranslator.Core.Api;

public static class DesktopNotificationPolicy
{
    // Public stable versions: build metadata is not a newer public release.
    public static Version? ParseStableVersion(string? value)
    {
        var text = (value ?? "").Trim().TrimStart('v', 'V').Split('+')[0];
        if (text.Contains('-') || !Version.TryParse(text, out var version)) return null;
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    }
    public static bool IsNewer(string? latest, string current) =>
        ParseStableVersion(latest) is { } remote && ParseStableVersion(current) is { } local && remote > local;

    public static Uri? DownloadAddress(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && string.IsNullOrEmpty(uri.UserInfo) ? uri : null;

    public static string AnnouncementFingerprint(string endpoint, string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint.TrimEnd('/') + "\n" + content.Trim())));
}
