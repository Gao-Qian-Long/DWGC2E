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
}
