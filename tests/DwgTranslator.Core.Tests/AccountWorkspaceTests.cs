using System;
using System.IO;
using DwgTranslator.Core.Services;
using DwgTranslator.Core.Translation;
using Xunit;
namespace DwgTranslator.Core.Tests;
public class AccountWorkspaceTests
{
 [Fact] public void IdentityIsStableScopedAndCannotEscapeRoot(){var root=Path.Combine(Path.GetTempPath(),"account-tests");var a=AccountWorkspace.DirectoryFor(root,"../../alice");Assert.StartsWith(Path.GetFullPath(root)+Path.DirectorySeparatorChar,a);Assert.Equal(a,AccountWorkspace.DirectoryFor(root,"../../alice"));Assert.NotEqual(a,AccountWorkspace.DirectoryFor(root,"bob"));Assert.NotEqual(a,AccountWorkspace.DirectoryFor(root,null));}
 [Fact] public void SwitchingCacheDoesNotExposePreviousAccountAndRestoresIt(){var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);try{var a=Path.Combine(root,"a.json");var b=Path.Combine(root,"b.json");var cache=new TranslationConsistencyService(a);cache.AddToCache("轴","shaft","ZH>EN");cache.SwitchAccountFile(b);Assert.False(cache.TryGetMatch("轴","ZH>EN",out _));cache.AddToCache("轴","axle","ZH>EN");cache.SwitchAccountFile(a);Assert.True(cache.TryGetMatch("轴","ZH>EN",out var value));Assert.Equal("shaft",value);cache.SwitchAccountFile(b);Assert.True(cache.TryGetMatch("轴","ZH>EN",out value));Assert.Equal("axle",value);}finally{Directory.Delete(root,true);}}
}
