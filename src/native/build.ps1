param()

$ErrorActionPreference = 'Stop'

Push-Location $PSScriptRoot

try {

    $vcRoot='C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC\14.41.34120'

    $sdkRoot='C:\Program Files (x86)\Windows Kits\10'

    $compiler=Join-Path $vcRoot 'bin\Hostx86\x64\cl.exe'

    $common=@('/nologo','/std:c++17','/EHsc','/O2','/MT',('/I'+$vcRoot+'\include'))

    foreach($folder in @('ucrt','shared','um','winrt')){$common+=('/I'+$sdkRoot+'\Include\10.0.22621.0\'+$folder)}

    $link=@('/link',('/LIBPATH:'+$vcRoot+'\lib\x64'),('/LIBPATH:'+$sdkRoot+'\Lib\10.0.22621.0\um\x64'),('/LIBPATH:'+$sdkRoot+'\Lib\10.0.22621.0\ucrt\x64'),'uuid.lib','user32.lib','imm32.lib','ole32.lib','oleaut32.lib')

    & $compiler @common /LD /DTT_WITH_TSF bridge.cpp ..\tsf-input-analysis\TsfObserver.cpp /Fe:TypeTrail.InputBridge.dll @link

    if ($LASTEXITCODE) { throw 'Bridge build failed' }

    Copy-Item -LiteralPath 'TypeTrail.InputBridge.dll' -Destination 'TypeTrail.InputBridge.1.7.13.r6.dll' -Force

} finally { Pop-Location }
