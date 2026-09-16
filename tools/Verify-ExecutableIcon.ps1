# Read PE resources without executing the candidate; compare every canonical ICO frame.
param([Parameter(Mandatory=$true)][string]$ExecutablePath,[string]$IconPath)
$ErrorActionPreference='Stop'
if(-not $IconPath){$IconPath=Join-Path $PSScriptRoot '../assets/icons/icon.ico'}
$exe=Get-Item -LiteralPath $ExecutablePath
if($exe.PSIsContainer -or $exe.Extension -ine '.exe'){throw 'Expected executable file'}
if(-not ('DwgTranslator.Tools.IconVerifier' -as [type])) {
Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
namespace DwgTranslator.Tools {
 public static class IconVerifier {
  delegate bool EnumName(IntPtr module, IntPtr type, IntPtr name, IntPtr param);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr LoadLibraryEx(string path,IntPtr file,uint flags);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern bool EnumResourceNames(IntPtr module,IntPtr type,EnumName callback,IntPtr param);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindResource(IntPtr module,IntPtr name,IntPtr type);
  [DllImport("kernel32.dll")] static extern uint SizeofResource(IntPtr module,IntPtr resource);
  [DllImport("kernel32.dll")] static extern IntPtr LoadResource(IntPtr module,IntPtr resource);
  [DllImport("kernel32.dll")] static extern IntPtr LockResource(IntPtr resource);
  [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
  public static int Verify(string exe,string icon) {
   byte[] ico=File.ReadAllBytes(icon);
   if(ico.Length<6 || BitConverter.ToUInt16(ico,0)!=0 || BitConverter.ToUInt16(ico,2)!=1) throw new InvalidDataException("Invalid ICO header");
   int count=BitConverter.ToUInt16(ico,4);
   int[] sizes={16,20,24,32,40,48,64,128,256};
   if(count!=sizes.Length || ico.Length<6+16*count) throw new InvalidDataException("Expected nine canonical icon sizes");
   var frames=new List<byte[]>();
   for(int i=0;i<count;i++) {
    int p=6+16*i, width=ico[p]==0?256:ico[p],height=ico[p+1]==0?256:ico[p+1];
    uint n=BitConverter.ToUInt32(ico,p+8),o=BitConverter.ToUInt32(ico,p+12);
    if(width!=sizes[i] || height!=sizes[i] || n==0 || o<6+16*count || (ulong)o+n>(ulong)ico.Length) throw new InvalidDataException("Invalid canonical icon frame");
    byte[] frame=new byte[(int)n]; Buffer.BlockCopy(ico,(int)o,frame,0,(int)n); frames.Add(frame);
   }
   IntPtr h=LoadLibraryEx(exe,IntPtr.Zero,2); // LOAD_LIBRARY_AS_DATAFILE: never run entry point.
   if(h==IntPtr.Zero) throw new InvalidDataException("Cannot read executable resources");
   try {
    var resources=new List<byte[]>();
    EnumName callback=delegate(IntPtr module,IntPtr type,IntPtr name,IntPtr param) {
     IntPtr r=FindResource(module,name,type); uint n=SizeofResource(module,r);
     IntPtr data=LockResource(LoadResource(module,r));
     if(data!=IntPtr.Zero && n>0 && n<=Int32.MaxValue){byte[] bytes=new byte[(int)n];Marshal.Copy(data,bytes,0,bytes.Length);resources.Add(bytes);} return true;
    };
    if(!EnumResourceNames(h,new IntPtr(3),callback,IntPtr.Zero)) throw new InvalidDataException("No icon resources");
    foreach(byte[] frame in frames) if(!resources.Any(x=>x.SequenceEqual(frame))) throw new InvalidDataException("Executable icon differs from canonical ICO");
    return count;
   } finally {FreeLibrary(h);}
  }
 }
}
"@
}
$count=[DwgTranslator.Tools.IconVerifier]::Verify($exe.FullName,(Get-Item -LiteralPath $IconPath).FullName)
Write-Output "EXECUTABLE_ICON=PASS; FRAMES=$count; PATH=$($exe.FullName)"
