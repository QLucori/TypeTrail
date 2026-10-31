param([string]$OutputDirectory = "$PSScriptRoot\bin",[switch]$ManagedOnly)

$ErrorActionPreference = 'Stop'

function CopyReleaseFile([string]$SourceFile,[string]$DestinationDirectory) {

    $target=Join-Path $DestinationDirectory ([IO.Path]::GetFileName($SourceFile))

    if ((Test-Path -LiteralPath $target) -and ((Get-FileHash -LiteralPath $target).Hash -eq (Get-FileHash -LiteralPath $SourceFile).Hash)) { return }

    Copy-Item -LiteralPath $SourceFile -Destination $target -Force

}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
. (Join-Path $PSScriptRoot 'scripts/Build-Managed.ps1')
$applicationRoot=Join-Path $PSScriptRoot 'src\TypeTrail'
. (Join-Path $PSScriptRoot 'scripts/Get-ApplicationSources.ps1')
$sourceFiles=@(Get-TypeTrailApplicationSources -ApplicationRoot $applicationRoot)
Invoke-TypeTrailManagedBuild -SourceRoot $PSScriptRoot -SourceFiles $sourceFiles -OutputPath (Join-Path $OutputDirectory 'TypeTrail.exe') -MainType 'TypeTrail.Program' -ApplicationResources

Copy-Item -LiteralPath (Join-Path $applicationRoot 'Configuration\TypeTrail.exe.config') -Destination $OutputDirectory -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'unicode-data\UNICODE-LICENSE.txt') -Destination $OutputDirectory -Force

if ($ManagedOnly) { Get-Item (Join-Path $OutputDirectory 'TypeTrail.exe') | Select-Object Name,Length; return }

$nativeBinary = Join-Path $PSScriptRoot 'src\native\TypeTrail.InputBridge.1.7.13.r6.dll'

$nativeSources=@(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src\native'),(Join-Path $PSScriptRoot 'src\tsf-input-analysis') -File | Where-Object { $_.Extension -in @('.cpp','.h','.ps1') })
$nativeNewest=($nativeSources | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).LastWriteTimeUtc
if (-not (Test-Path -LiteralPath $nativeBinary) -or (Get-Item -LiteralPath $nativeBinary).LastWriteTimeUtc -lt $nativeNewest) { & (Join-Path $PSScriptRoot 'src\native\build.ps1') }

CopyReleaseFile $nativeBinary $OutputDirectory

$companionExe=Join-Path $PSScriptRoot 'src\companion\bin\TypeTrail.InputHost.x86.exe'
$companionDll=Join-Path $PSScriptRoot 'src\companion\bin\TypeTrail.InputBridge.1.7.13.r6.x86.dll'
$companionSources=@($nativeSources)+@(Get-Item -LiteralPath (Join-Path $PSScriptRoot 'src\companion\NativeBridge.cs'),(Join-Path $PSScriptRoot 'src\companion\InputHost.cs'),(Join-Path $PSScriptRoot 'src\companion\build.ps1'))
$companionNewest=($companionSources | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).LastWriteTimeUtc
if (-not (Test-Path -LiteralPath $companionDll) -or (Get-Item -LiteralPath $companionDll).LastWriteTimeUtc -lt $nativeNewest) { & (Join-Path $PSScriptRoot 'src\companion\build.ps1') }
elseif (-not (Test-Path -LiteralPath $companionExe) -or (Get-Item -LiteralPath $companionExe).LastWriteTimeUtc -lt $companionNewest) { & (Join-Path $PSScriptRoot 'src\companion\build.ps1') -ManagedOnly }

foreach ($name in @('TypeTrail.InputBridge.1.7.13.r6.x86.dll','TypeTrail.InputHost.x86.exe')) {

    $companion = Join-Path $PSScriptRoot ('src\companion\bin\' + $name)

    CopyReleaseFile $companion $OutputDirectory

}

Get-Item (Join-Path $OutputDirectory 'TypeTrail.exe') | Select-Object Name,Length

