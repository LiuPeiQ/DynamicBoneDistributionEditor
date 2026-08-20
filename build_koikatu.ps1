[CmdletBinding()]
param(
    [string]$KoikatuRoot = 'E:\Koikatu\Koikatu',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts\kk-release')
)

$ErrorActionPreference = 'Stop'

$compiler = Get-ChildItem (Join-Path $env:ProgramFiles 'dotnet\sdk') -Recurse -Filter 'csc.dll' |
    Sort-Object FullName -Descending |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $compiler) {
    throw 'Roslyn csc.dll was not found under the installed .NET SDKs.'
}

$managed = Join-Path $KoikatuRoot 'Koikatu_Data\Managed'
$packages = Join-Path $PSScriptRoot 'src\packages'
$sources = @(
    Get-ChildItem (Join-Path $PSScriptRoot 'src\Core.DynamicBoneDistributionEditor') -File -Filter '*.cs' |
        Select-Object -ExpandProperty FullName
) + @((Join-Path $PSScriptRoot 'src\KK.DynamicBoneDistributionEditor\Properties\AssemblyInfo.cs'))

$references = @(
    (Join-Path $managed 'mscorlib.dll'),
    (Join-Path $managed 'System.dll'),
    (Join-Path $managed 'System.Core.dll'),
    (Join-Path $managed 'System.Xml.dll'),
    (Join-Path $managed 'System.Xml.Linq.dll'),
    (Join-Path $packages 'ExtensibleSaveFormat.Koikatu.16.8.1\lib\net35\ExtensibleSaveFormat.dll'),
    (Join-Path $packages 'IllusionLibs.BepInEx.Harmony.2.9.0\lib\net35\0Harmony.dll'),
    (Join-Path $packages 'IllusionLibs.BepInEx.5.4.20\lib\net35\BepInEx.dll'),
    (Join-Path $packages 'IllusionLibs.Koikatu.Assembly-CSharp.2019.4.27.4\lib\net35\Assembly-CSharp.dll'),
    (Join-Path $packages 'IllusionLibs.Koikatu.Assembly-CSharp-firstpass.2019.4.27.4\lib\net35\Assembly-CSharp-firstpass.dll'),
    (Join-Path $packages 'IllusionLibs.Koikatu.UnityEngine.5.6.2.4\lib\net35\UnityEngine.dll'),
    (Join-Path $packages 'IllusionLibs.Koikatu.UnityEngine.UI.5.6.2.4\lib\net35\UnityEngine.UI.dll'),
    (Join-Path $packages 'IllusionModdingAPI.KKAPI.1.38.0\lib\net35\KKAPI.dll'),
    (Join-Path $packages 'AnimationCurveEditor.Old.dll'),
    (Join-Path $packages 'KKPE.dll'),
    (Join-Path $packages 'KK_Fix_MakerOptimizations.dll'),
    (Join-Path $packages 'Screencap.dll')
)

$missing = $references | Where-Object { -not (Test-Path -LiteralPath $_) }
if ($missing) {
    throw "Missing references:`n$($missing -join [Environment]::NewLine)"
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$output = Join-Path $OutputDirectory 'KK_DynamicBoneDistributionEditor.dll'
$arguments = @(
    '/noconfig', '/nostdlib+', '/target:library', '/platform:anycpu',
    '/optimize+', '/debug:pdbonly', '/langversion:7.3', '/define:KK',
    "/out:$output"
)
$arguments += $references | ForEach-Object { "/reference:$_" }
$arguments += $sources

& dotnet exec $compiler @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Koikatu DBDE build failed with exit code $LASTEXITCODE."
}

Get-Item $output, ([IO.Path]::ChangeExtension($output, '.pdb')) |
    Select-Object FullName, Length, LastWriteTime
