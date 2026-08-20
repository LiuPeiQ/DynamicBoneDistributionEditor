[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$SnapshotDirectory = 'E:\Koikatu\Koikatu\BepInEx\config\DBDE_RuntimeSnapshots',
    [ValidateRange(0, 1000)]
    [int]$KeepNewest = 1,
    [switch]$DeleteAll
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $SnapshotDirectory -PathType Container)) {
    throw "Snapshot directory does not exist: $SnapshotDirectory"
}

$snapshotRoot = (Resolve-Path -LiteralPath $SnapshotDirectory).Path.TrimEnd('\')
$patterns = @(
    'DBDE_LowerBodySkirtSequence_*.json',
    'DBDE_LowerBodySkirtSequence_*.json.disabled'
)

$allSequences = @(
    foreach ($pattern in $patterns) {
        Get-ChildItem -LiteralPath $snapshotRoot -File -Filter $pattern
    }
) | Sort-Object -Property LastWriteTime -Descending -Unique

if ($DeleteAll) {
    $KeepNewest = 0
}

$targets = @($allSequences | Select-Object -Skip $KeepNewest)
if ($targets.Count -eq 0) {
    Write-Host "Nothing to delete. Found $($allSequences.Count) sequence file(s); keeping $KeepNewest."
    return
}

foreach ($target in $targets) {
    $resolved = (Resolve-Path -LiteralPath $target.FullName).Path
    if (-not $resolved.StartsWith($snapshotRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to delete outside the snapshot directory: $resolved"
    }
    if ($PSCmdlet.ShouldProcess($resolved, 'Delete old DBDE runtime sequence')) {
        Remove-Item -LiteralPath $resolved -Force
        Write-Host "Deleted: $($target.Name)"
    }
}

Write-Host "Finished. Deleted $($targets.Count) old sequence file(s); kept $KeepNewest newest file(s)."
