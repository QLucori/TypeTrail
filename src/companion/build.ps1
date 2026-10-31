param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'bin'),[switch]$ManagedOnly)
$ErrorActionPreference = 'Stop'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$libraryMatch=[regex]::Match((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'NativeBridge.cs') -Raw),'const string Library\s*=\s*"([^"]+)"')
if(-not $libraryMatch.Success){throw 'Native bridge library name is missing'}
$libraryName=$libraryMatch.Groups[1].Value
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$vcRoot='C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC\14.41.34120'
$sdkRoot='C:\Program Files (x86)\Windows Kits\10'
$compiler=Join-Path $vcRoot 'bin\Hostx64\x86\cl.exe'
$common=@('/nologo','/std:c++17','/EHsc','/O2','/MT',('/I'+$vcRoot+'\include'),('/Fo'+$OutputDirectory+'\'))
foreach($folder in @('ucrt','shared','um','winrt')){$common+=('/I'+$sdkRoot+'\Include\10.0.22621.0\'+$folder)}
$link=@('/link',('/LIBPATH:'+$vcRoot+'\lib\x86'),('/LIBPATH:'+$sdkRoot+'\Lib\10.0.22621.0\um\x86'),('/LIBPATH:'+$sdkRoot+'\Lib\10.0.22621.0\ucrt\x86'),'uuid.lib','user32.lib','imm32.lib','ole32.lib','oleaut32.lib')
if(-not $ManagedOnly) {
 & $compiler @common /LD /DTT_WITH_TSF (Join-Path $PSScriptRoot '..\native\bridge.cpp') (Join-Path $PSScriptRoot '..\tsf-input-analysis\TsfObserver.cpp') ('/Fe:'+ (Join-Path $OutputDirectory $libraryName)) @link
 if ($LASTEXITCODE) { throw 'x86 native bridge build failed' }
}
$csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /platform:x86 /optimize+ ('/out:'+$OutputDirectory+'\TypeTrail.InputHost.x86.exe') /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'NativeBridge.cs') (Join-Path $PSScriptRoot 'InputHost.cs')
if ($LASTEXITCODE) { throw 'x86 companion build failed' }
Get-Item -LiteralPath (Join-Path $OutputDirectory 'TypeTrail.InputHost.x86.exe') | Select-Object Name,Length
if(Test-Path -LiteralPath (Join-Path $OutputDirectory $libraryName)){Get-Item -LiteralPath (Join-Path $OutputDirectory $libraryName) | Select-Object Name,Length}
