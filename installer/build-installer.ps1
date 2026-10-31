param(

    [Parameter(Mandatory=$true)][string]$CompilerPath,

    [string]$PayloadDirectory,

    [string]$OutputDirectory

)

$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

if (-not $PayloadDirectory) { $PayloadDirectory = Join-Path $repo 'bin' }

if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo 'dist' }

$PayloadDirectory = (Resolve-Path -LiteralPath $PayloadDirectory).Path

$CompilerPath = (Resolve-Path -LiteralPath $CompilerPath).Path

$stage = Join-Path $repo '.build\installer\payload'

New-Item -ItemType Directory -Force -Path $stage,$OutputDirectory | Out-Null

foreach ($name in @('TypeTrail.exe','TypeTrail.exe.config','TypeTrail.InputBridge.1.7.13.r6.dll','TypeTrail.InputBridge.1.7.13.r6.x86.dll','TypeTrail.InputHost.x86.exe','使用说明.txt','README-English.txt','UNICODE-LICENSE.txt')) {

    $source = Join-Path $PayloadDirectory $name
    if (-not (Test-Path -LiteralPath $source) -and $name -in @('使用说明.txt','README-English.txt')) { $source = Join-Path (Join-Path $repo 'docs') $name }
    Copy-Item -LiteralPath $source -Destination $stage -Force

}

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

& $csc /nologo /target:winexe /platform:x64 /optimize+ "/out:$stage\TypeTrail.SetupHelper.exe" (Join-Path $PSScriptRoot 'SetupHelper.cs') (Join-Path $PSScriptRoot '..\src\TypeTrail\Bootstrap\ProfileIdentity.cs')

if ($LASTEXITCODE -ne 0) { throw 'Setup helper compilation failed.' }

Add-Type -AssemblyName System.Drawing

$brand = New-Object Drawing.Bitmap(492,942)

$graphics = [Drawing.Graphics]::FromImage($brand)

$gradient = New-Object Drawing.Drawing2D.LinearGradientBrush((New-Object Drawing.Rectangle(0,0,492,942)),[Drawing.ColorTranslator]::FromHtml('#403178'),[Drawing.ColorTranslator]::FromHtml('#171421'),90.0)

$logo = [Drawing.Image]::FromFile((Join-Path $PSScriptRoot '..\assets\TypeTrail.png'))

try {

    $graphics.FillRectangle($gradient,0,0,492,942)

    $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

    $graphics.DrawImage($logo,126,234,240,240)

    $brand.Save((Join-Path $stage 'wizard-brand.png'),[Drawing.Imaging.ImageFormat]::Png)

} finally {

    $logo.Dispose(); $gradient.Dispose(); $graphics.Dispose(); $brand.Dispose()

}

$compilerArgs = @('/Qp', ('/DPayloadDir='+$stage), ('/DOutputDir='+[IO.Path]::GetFullPath($OutputDirectory)))

$compilerArgs += Join-Path $PSScriptRoot 'TypeTrail.iss'

& $CompilerPath @compilerArgs

if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }

