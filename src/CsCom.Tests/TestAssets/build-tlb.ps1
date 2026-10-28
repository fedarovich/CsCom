<#
.SYNOPSIS
    Compiles CsComTest.idl into CsComTest.tlb with MIDL and imports it into Interop.CsComTest.dll with TlbImp.

.DESCRIPTION
    Requires Visual Studio with the C++ build tools (MIDL uses the C preprocessor) and the Windows SDK with the .NET Framework
    tools (TlbImp). The compiled type library and the interop assembly are committed, so this script only has to be run after
    changing the IDL.

    TlbImp reports warnings for the parts of the type library that it cannot import, which the tests expect: a platform
    agnostic assembly (TI3002), the properties and _NewEnum of the source dispinterface DShapeEvents (TI3013, TI3007) and the
    pointer to a structure in Record.Next (TI3016).
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vsPath = & $vswhere -latest -prerelease -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) {
    throw 'Visual Studio with the C++ build tools is required.'
}

$tlbImp = Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} 'Microsoft SDKs\Windows') -Recurse -Filter TlbImp.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.Directory.Name -ne 'x64' } |
    Sort-Object FullName -Descending |
    Select-Object -First 1
if (-not $tlbImp) {
    throw 'TlbImp.exe of the Windows SDK (.NET Framework tools) is required.'
}

$output = Join-Path ([System.IO.Path]::GetTempPath()) "cscom-midl-$([guid]::NewGuid())"
New-Item -ItemType Directory -Path $output | Out-Null
try {
    $command = "`"$vsPath\Common7\Tools\VsDevCmd.bat`" -arch=amd64 -host_arch=amd64 -no_logo && midl /nologo /env x64 /out `"$output`" /tlb CsComTest.tlb `"$PSScriptRoot\CsComTest.idl`""
    & cmd.exe /c $command
    if ($LASTEXITCODE -ne 0) {
        throw "MIDL failed with exit code $LASTEXITCODE."
    }

    $typeLibrary = Join-Path $PSScriptRoot 'CsComTest.tlb'
    Copy-Item (Join-Path $output 'CsComTest.tlb') $typeLibrary -Force
    Write-Host "Created $typeLibrary"

    $assembly = Join-Path $PSScriptRoot 'Interop.CsComTest.dll'
    & $tlbImp.FullName $typeLibrary /nologo /namespace:CsComTest "/out:$assembly"
    if ($LASTEXITCODE -ne 0) {
        throw "TlbImp failed with exit code $LASTEXITCODE."
    }
}
finally {
    Remove-Item $output -Recurse -Force -ErrorAction SilentlyContinue
}
