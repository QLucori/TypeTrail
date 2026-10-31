function Get-TypeTrailApplicationSources {
    param([Parameter(Mandatory=$true)][string]$ApplicationRoot)
    $root=(Resolve-Path -LiteralPath $ApplicationRoot).Path
    $prefix=$root.TrimEnd('\')+'\'
    $manifest=Join-Path $root 'sources.txt'
    $ordered=New-Object 'System.Collections.Generic.List[string]'
    $declared=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach($line in Get-Content -LiteralPath $manifest -Encoding UTF8){
        $relative=$line.Trim()
        if(-not $relative -or $relative.StartsWith('#')){continue}
        if([IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or $relative -match '(^|[\\/])\.\.([\\/]|$)' -or -not $relative.EndsWith('.cs',[StringComparison]::OrdinalIgnoreCase)){
            throw ('Invalid application source path: '+$relative)
        }
        $path=(Resolve-Path -LiteralPath (Join-Path $root $relative)).Path
        if(-not $path.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or -not $declared.Add($path)){
            throw ('Duplicate or out-of-tree application source: '+$relative)
        }
        $ordered.Add($path)
    }
    $actual=@((Get-ChildItem -LiteralPath $root -Filter '*.cs' -Recurse -File).FullName)
    if($ordered.Count -eq 0 -or -not $declared.SetEquals([string[]]$actual)){
        throw 'The application source manifest must include every C# source exactly once. Update src/TypeTrail/sources.txt.'
    }
    $ordered.ToArray()
}
