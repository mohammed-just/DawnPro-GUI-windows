[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ExpectedDirectory,

    [Parameter(Mandatory = $true)]
    [string] $ActualDirectory
)

$ErrorActionPreference = 'Stop'

function Get-TreeEntries {
    param([Parameter(Mandatory = $true)][string] $Root)

    $resolvedRoot = (Resolve-Path -LiteralPath $Root).Path
    $entries = @{}
    $rootPrefix = $resolvedRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    foreach ($file in Get-ChildItem -LiteralPath $resolvedRoot -File -Recurse) {
        if (-not $file.FullName.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Enumerated file is outside the comparison root: $($file.FullName)"
        }
        $relativePath = $file.FullName.Substring($rootPrefix.Length).Replace('\', '/')
        $entries[$relativePath] = [pscustomobject]@{
            Sha256          = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            Size            = $file.Length
            LastWriteTimeUtc = $file.LastWriteTimeUtc.ToString('O')
        }
    }
    return $entries
}

$expectedRoot = (Resolve-Path -LiteralPath $ExpectedDirectory).Path
$actualRoot = (Resolve-Path -LiteralPath $ActualDirectory).Path
$expected = Get-TreeEntries -Root $expectedRoot
$actual = Get-TreeEntries -Root $actualRoot
$relativePaths = @($expected.Keys) + @($actual.Keys) | Sort-Object -Unique
$mismatches = foreach ($relativePath in $relativePaths) {
    $expectedExists = $expected.ContainsKey($relativePath)
    $actualExists = $actual.ContainsKey($relativePath)
    $expectedEntry = if ($expectedExists) { $expected[$relativePath] } else { $null }
    $actualEntry = if ($actualExists) { $actual[$relativePath] } else { $null }
    if (-not $expectedExists -or
        -not $actualExists -or
        $expectedEntry.Size -ne $actualEntry.Size -or
        $expectedEntry.Sha256 -cne $actualEntry.Sha256) {
        [pscustomobject]@{
            RelativePath               = $relativePath
            ExpectedFullPath           = [System.IO.Path]::GetFullPath((Join-Path $expectedRoot $relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)))
            ActualFullPath             = [System.IO.Path]::GetFullPath((Join-Path $actualRoot $relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)))
            ExpectedExists             = $expectedExists
            ActualExists               = $actualExists
            ExpectedSha256             = if ($expectedExists) { $expectedEntry.Sha256 } else { $null }
            ActualSha256               = if ($actualExists) { $actualEntry.Sha256 } else { $null }
            ExpectedSize               = if ($expectedExists) { $expectedEntry.Size } else { $null }
            ActualSize                 = if ($actualExists) { $actualEntry.Size } else { $null }
            ExpectedLastWriteTimeUtc   = if ($expectedExists) { $expectedEntry.LastWriteTimeUtc } else { $null }
            ActualLastWriteTimeUtc     = if ($actualExists) { $actualEntry.LastWriteTimeUtc } else { $null }
        }
    }
}

[pscustomobject]@{
    ExpectedDirectory = $expectedRoot
    ActualDirectory   = $actualRoot
    ExpectedFileCount = $expected.Count
    ActualFileCount   = $actual.Count
    MismatchCount     = @($mismatches).Count
    Mismatches        = @($mismatches)
} | ConvertTo-Json -Depth 4

if (@($mismatches).Count -ne 0) {
    exit 1
}
