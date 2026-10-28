<#
.SYNOPSIS
    Compiles the test type libraries with MIDL: CsComTest.idl into CsComTest.tlb, which TlbImp imports into
    Interop.CsComTest.dll, and CsComDrawing.idl into CsComDrawing.tlb.

.DESCRIPTION
    Requires Visual Studio with the C++ build tools (MIDL uses the C preprocessor) and the Windows SDK with the .NET Framework
    tools (TlbImp). The compiled type libraries and the interop assembly are committed, so this script only has to be run after
    changing an IDL file, for that library.

    TlbImp reports warnings for the parts of the type library that it cannot import, which the tests expect: a platform
    agnostic assembly (TI3002), the properties and _NewEnum of the source dispinterface DShapeEvents (TI3013, TI3007) and the
    pointer to a structure in Record.Next (TI3016).
.PARAMETER Library
    The libraries to compile: CsComTest, CsComDrawing, or both by default.
#>
[CmdletBinding()]
param(
    [ValidateSet('CsComTest', 'CsComDrawing')]
    [string[]] $Library = @('CsComTest', 'CsComDrawing')
)

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
    foreach ($name in $Library) {
        $command = "`"$vsPath\Common7\Tools\VsDevCmd.bat`" -arch=amd64 -host_arch=amd64 -no_logo && midl /nologo /env x64 /out `"$output`" /tlb $name.tlb `"$PSScriptRoot\$name.idl`""
        & cmd.exe /c $command
        if ($LASTEXITCODE -ne 0) {
            throw "MIDL failed with exit code $LASTEXITCODE."
        }

        $typeLibrary = Join-Path $PSScriptRoot "$name.tlb"
        Copy-Item (Join-Path $output "$name.tlb") $typeLibrary -Force
        Write-Host "Created $typeLibrary"

        if ($name -eq 'CsComTest') {
            $assembly = Join-Path $PSScriptRoot 'Interop.CsComTest.dll'
            & $tlbImp.FullName $typeLibrary /nologo /namespace:CsComTest "/out:$assembly"
            if ($LASTEXITCODE -ne 0) {
                throw "TlbImp failed with exit code $LASTEXITCODE."
            }
        }
    }
}
finally {
    Remove-Item $output -Recurse -Force -ErrorAction SilentlyContinue
}
