function Invoke-TypeTrailManagedBuild {
    param(
        [Parameter(Mandatory=$true)][string]$SourceRoot,
        [Parameter(Mandatory=$true)][string[]]$SourceFiles,
        [Parameter(Mandatory=$true)][string]$OutputPath,
        [string]$MainType,
        [string[]]$ExtraReferences=@(),
        [switch]$ApplicationResources
    )
    $applicationRoot=Join-Path $SourceRoot 'src\TypeTrail'
    $framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    $references=@('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Security.dll','System.Web.Extensions.dll','System.Xaml.dll','Accessibility.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll','WPF\UIAutomationClient.dll','WPF\UIAutomationTypes.dll','WPF\UIAutomationProvider.dll')
    $arguments=@('/nologo','/target:winexe','/platform:x64','/optimize+','/utf8output',('/out:'+$OutputPath),('/win32manifest:'+(Join-Path $applicationRoot 'Properties\app.manifest')))
    if($MainType){$arguments+='/main:'+$MainType}
    if($ApplicationResources){
        $arguments+=('/resource:'+(Join-Path $applicationRoot 'Presentation\Window.xaml')+',TypeTrail.Window.xaml')
        $arguments+=('/win32icon:'+(Join-Path $SourceRoot 'assets\TypeTrail.ico'))
        $arguments+=('/resource:'+(Join-Path $SourceRoot 'assets\TypeTrail.ico')+',TypeTrail.Icon.ico')
        $arguments+=('/resource:'+(Join-Path $SourceRoot 'assets\TypeTrail.png')+',TypeTrail.Icon.png')
    }
    foreach($reference in $references){$arguments+='/reference:'+(Join-Path $framework $reference)}
    foreach($reference in $ExtraReferences){$arguments+='/reference:'+$reference}
    $arguments+=$SourceFiles
    & (Join-Path $framework 'csc.exe') @arguments
    if($LASTEXITCODE -ne 0){throw 'Managed compilation failed'}
}
