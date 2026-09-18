using System.IO;
using System.Net.Http;
using DwgTranslator.Core.Api;
using Serilog;
namespace DwgTranslator.App.ViewModels;
public partial class MainViewModel
{
    private string? _readAnnouncement;
    private string AnnouncementReadPath => Path.Combine(App.AppDataDir, "announcement-read.txt");
    public async Task<string> ReadSiteAnnouncementAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        return await SiteAnnouncementClient.ReadAsync(http, _config.ApiBaseUrl, cancellationToken);
    }
    private string AnnouncementFingerprint(string content) => DesktopNotificationPolicy.AnnouncementFingerprint(
        SiteAnnouncementClient.GetEndpoint(_config.ApiBaseUrl).AbsoluteUri, content);
    public bool IsAnnouncementUnread(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        try { _readAnnouncement ??= File.Exists(AnnouncementReadPath) ? File.ReadAllText(AnnouncementReadPath).Trim() : ""; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
        return _readAnnouncement != AnnouncementFingerprint(content);
    }
    public void MarkAnnouncementRead(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        _readAnnouncement = AnnouncementFingerprint(content);
        try
        {
            Directory.CreateDirectory(App.AppDataDir);
            var temporary = AnnouncementReadPath + ".tmp";
            File.WriteAllText(temporary, _readAnnouncement);
            File.Move(temporary, AnnouncementReadPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Log.Warning("公告已读状态未能保存：{ErrorType}", ex.GetType().Name); }
    }
}
