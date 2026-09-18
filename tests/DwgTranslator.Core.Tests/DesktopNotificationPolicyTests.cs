using DwgTranslator.Core.Api;
namespace DwgTranslator.Core.Tests;
public sealed class DesktopNotificationPolicyTests
{
    [Theory]
    [InlineData("2.1.2", "2.1.1", true)]
    [InlineData("2.10.0", "2.9.9", true)]
    [InlineData("2.1.1", "2.1.1+ui.20260918-123456.abc", false)]
    [InlineData("v2.1.1", "2.1.1.0", false)]
    [InlineData("2.0.9", "2.1.1", false)]
    [InlineData("2.2.0-beta", "2.1.1", false)]
    [InlineData("garbage", "2.1.1", false)]
    [InlineData("", "2.1.1", false)]
    public void ComparesStableVersions(string latest, string current, bool expected) => Assert.Equal(expected, DesktopNotificationPolicy.IsNewer(latest, current));
    [Theory]
    [InlineData("https://example.test/setup.exe", true)]
    [InlineData("http://example.test/setup.exe", false)]
    [InlineData("file:///C:/bad.exe", false)]
    [InlineData("https://name:secret@example.test", false)]
    [InlineData("", false)]
    public void OnlySafeDownloadLinks(string value, bool valid) => Assert.Equal(valid, DesktopNotificationPolicy.DownloadAddress(value) != null);
    [Fact] public void ReadFingerprintTracksContentAndSource()
    {
        var first = DesktopNotificationPolicy.AnnouncementFingerprint("https://example.test/v1/site", "公告");
        Assert.Equal(first, DesktopNotificationPolicy.AnnouncementFingerprint("https://example.test/v1/site/", " 公告 "));
        Assert.NotEqual(first, DesktopNotificationPolicy.AnnouncementFingerprint("https://example.test/v1/site", "新公告"));
        Assert.NotEqual(first, DesktopNotificationPolicy.AnnouncementFingerprint("https://other.test/v1/site", "公告"));
    }
}
