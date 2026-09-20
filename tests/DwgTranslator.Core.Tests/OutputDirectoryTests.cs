using System;
using System.IO;
using System.Text.Json;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using Xunit;
namespace DwgTranslator.Core.Tests;
public class OutputDirectoryTests
{
 private readonly string root = Path.Combine(Path.GetTempPath(), "output-directory-tests");
 [Fact] public void SavedAbsoluteDefaultSurvivesSerialization()
 {
  var path=Path.Combine(root,"custom");
  var config=new AppConfig {ActiveAccountId="alice",ExportDirectory="exports"};
  config.AccountOutputDirectories["alice"]=path;
  var restored=JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config))!;
  Assert.Equal(path,AccountWorkspace.OutputDirectoryFor(restored,root));
 }
 [Fact] public void AnotherAccountCannotInheritPreviousAccountsDefault()
 {
  var config=new AppConfig {ActiveAccountId="bob",ExportDirectory=Path.Combine(root,"alice-custom")};
  config.AccountOutputDirectories["alice"]=config.ExportDirectory;
  Assert.Equal(string.Empty,AccountWorkspace.OutputDirectoryFor(config,root));
  config.ActiveAccountId="alice";
  Assert.Equal(config.ExportDirectory,AccountWorkspace.OutputDirectoryFor(config,root));
 }
 [Fact] public void LegacyAbsoluteDefaultIsPreservedForOriginalAccount()
 {
  var config=new AppConfig {ActiveAccountId="alice",ExportDirectory=Path.Combine(root,"legacy")};
  Assert.Equal(config.ExportDirectory,AccountWorkspace.OutputDirectoryFor(config,root));
 }
 [Fact] public void RelativeDefaultFallsBackToOwnedWorkspace()
 {
  var config=new AppConfig {ActiveAccountId="alice",ExportDirectory="exports"};
  config.AccountOutputDirectories["alice"]="relative-folder";
  Assert.Equal(string.Empty,AccountWorkspace.OutputDirectoryFor(config,root));
 }
 // ==== 默认输出目录不再落在产品数据目录里（用户反馈：不要写进 AppData/安装目录的 exports）====
 [Fact] public void ProductAreaExportsIsNotAUserChoice()
 {
  // 老版本把 root\exports 当默认输出并写进 settings.json；它会被原样迁移过来，
  // 但那是"未被选择过的默认值"，不是用户的选择 —— 必须按未设置处理。
  var config=new AppConfig {ActiveAccountId="alice",ExportDirectory=Path.Combine(root,"exports")};
  Assert.Equal(string.Empty,AccountWorkspace.OutputDirectoryFor(config,root));
 }
 [Fact] public void ProductAreaDefaultMigratesToSourceFolder()
 {
  var config=new AppConfig {ActiveAccountId="alice",ExportDirectory=Path.Combine(root,"exports")};
  var source=Path.Combine(root,"drawings");
  Assert.Equal(Path.Combine(source,"已翻译图纸"),
   AccountWorkspace.OutputDirectoryFor(config,root,source,"已翻译图纸"));
 }
 [Fact] public void DeliberateChoiceInsideProductAreaIsStillHonoured()
 {
  // 只有 exports 这个旧默认值作废；用户在数据目录里显式选过的目录（哪怕是同层）照旧生效。
  var chosen=Path.Combine(root,"legacy-archive");
  var config=new AppConfig {ActiveAccountId="alice",ExportDirectory=chosen};
  Assert.Equal(chosen,AccountWorkspace.OutputDirectoryFor(config,root));
 }
 [Fact] public void SavedAccountDirectoryBeatsBothLegacyAndSourceDefault()
 {
  var saved=Path.Combine(root,"D-drive","project-a");
  var config=new AppConfig {ActiveAccountId="alice",ExportDirectory=Path.Combine(root,"exports")};
  config.AccountOutputDirectories["alice"]=saved;
  Assert.Equal(saved,AccountWorkspace.OutputDirectoryFor(config,root,Path.Combine(root,"drawings"),"已翻译图纸"));
 }
 [Fact] public void WithoutAConfiguredDirectoryOutputLandsBesideTheSourceDrawing()
 {
  var config=new AppConfig {ActiveAccountId="alice"};
  var source=Path.Combine(root,"drawings");
  Assert.Equal(Path.Combine(source,"已翻译图纸"),
   AccountWorkspace.OutputDirectoryFor(config,root,source,"已翻译图纸"));
 }
 [Fact] public void SourceFolderDefaultIsSkippedWhenTheSourceDirectoryIsUnknown()
 {
  // 算不出源目录时保持旧行为：返回空串，由导出入口引导用户选一次。
  var config=new AppConfig {ActiveAccountId="alice"};
  Assert.Equal(string.Empty,AccountWorkspace.OutputDirectoryFor(config,root));
  Assert.Equal(string.Empty,AccountWorkspace.OutputDirectoryFor(config,root,"   ","已翻译图纸"));
 }
 [Fact] public void DefaultBesideSourceCanTargetTheSourceFolderItself()
 {
  // 不给子目录名时直接用源目录：调用方可以决定"就地输出"。
  var source=Path.Combine(root,"drawings");
  Assert.Equal(Path.GetFullPath(source),AccountWorkspace.DefaultBesideSource(source,null));
  Assert.Equal(string.Empty,AccountWorkspace.DefaultBesideSource(null,"已翻译图纸"));
 }
 // ==== 2026-10-02 用户决定：默认输出 = <安装目录>\exports（发布即生效，无需重装）====
 [Fact] public void InstallExportsIsTheDefaultWhenNothingWasChosen()
 {
  // 没选过目录时优先落在安装区 exports，即使能算出源图纸目录。
  var install=Path.Combine(root,"install-a");
  var config=new AppConfig {ActiveAccountId="alice"};
  var source=Path.Combine(root,"drawings");
  Assert.Equal(Path.Combine(install,"exports"),
   AccountWorkspace.OutputDirectoryFor(config,root,source,"已翻译图纸",install));
 }
 [Fact] public void SavedLegacyRoamingExportsIsNotAUserChoice()
 {
  // 旧版把 %AppData%\DwgTranslator\exports 持久化进了 AccountOutputDirectories；
  // 它是旧默认值，不是用户的选择 —— 必须按未设置处理并迁到安装区 exports。
  var roaming=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"DwgTranslator","exports");
  var install=Path.Combine(root,"install-b");
  var config=new AppConfig {ActiveAccountId="alice"};
  config.AccountOutputDirectories["alice"]=roaming;
  Assert.Equal(Path.Combine(install,"exports"),
   AccountWorkspace.OutputDirectoryFor(config,root,null,null,install));
 }
 [Fact] public void SavedInstallExportsIsStableAsAUserChoice()
 {
  // 新默认值本身被持久化后（切账号时会保存当前解析结果）依旧原样返回，不来回跳。
  var install=Path.Combine(root,"install-c");
  var config=new AppConfig {ActiveAccountId="alice"};
  config.AccountOutputDirectories["alice"]=Path.Combine(install,"exports");
  Assert.Equal(Path.Combine(install,"exports"),
   AccountWorkspace.OutputDirectoryFor(config,root,null,null,install));
 }
 [Fact] public void UnwritableInstallFallsBackToBesideSource()
 {
  // 安装区探测以路径指向一个文件的方式模拟"不可写"：退回源图纸旁边。
  var notADirectory=Path.Combine(root,"blocked-file");
  File.WriteAllText(notADirectory,"x");
  var config=new AppConfig {ActiveAccountId="alice"};
  var source=Path.Combine(root,"drawings");
  Assert.Equal(Path.Combine(source,"已翻译图纸"),
   AccountWorkspace.OutputDirectoryFor(config,root,source,"已翻译图纸",notADirectory));
 }
}
