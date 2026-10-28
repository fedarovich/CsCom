<#
.SYNOPSIS
    Compiles CsComTest.idl into CsComTest.tlb with MIDL.

.DESCRIPTION
    Requires Visual Studio with the C++ build tools (MIDL uses the C preprocessor) and the Windows SDK.
    The compiled type library is committed, so this script only has to be run after changing the IDL.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vsPath = & $vswhere -latest -prerelease -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) {
    throw 'Visual Studio with the C++ build tools is required.'
}

$output = Join-Path ([System.IO.Path]::GetTempPath()) "cscom-midl-$([guid]::NewGuid())"
New-Item -ItemType Directory -Path $output | Out-Null
try {
    $command = "`"$vsPath\Common7\Tools\VsDevCmd.bat`" -arch=amd64 -host_arch=amd64 -no_logo && midl /nologo /env x64 /out `"$output`" /tlb CsComTest.tlb `"$PSScriptRoot\CsComTest.idl`""
    & cmd.exe /c $command
    if ($LASTEXITCODE -ne 0) {
        throw "MIDL failed with exit code $LASTEXITCODE."
    }

    Copy-Item (Join-Path $output 'CsComTest.tlb') (Join-Path $PSScriptRoot 'CsComTest.tlb') -Force
    Write-Host "Created $(Join-Path $PSScriptRoot 'CsComTest.tlb')"
}
finally {
    Remove-Item $output -Recurse -Force -ErrorAction SilentlyContinue
}
