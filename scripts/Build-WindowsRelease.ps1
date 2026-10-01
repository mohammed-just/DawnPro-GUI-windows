#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string]$DotnetPath = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repositoryRoot 'artifacts\releases' }
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$dotnetExecutable = (Get-Command $DotnetPath -CommandType Application -ErrorAction Stop).Source
$dotnetRoot = Split-Path -Parent $dotnetExecutable
$sdkVersion = (& $dotnetExecutable --version).Trim()
$expectedSdk = (Get-Content -LiteralPath (Join-Path $repositoryRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
if ($LASTEXITCODE -ne 0 -or $sdkVersion -ne $expectedSdk) { throw "Use the pinned .NET SDK $expectedSdk; found $sdkVersion." }
[xml]$project = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\Moondrop.Wpf\Moondrop.Wpf.csproj') -Raw
$version = @($project.Project.PropertyGroup.Version | Where-Object { $_ })[0]
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a stable application version.' }
$sourceRevision = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceRevision -notmatch '^[0-9a-f]{40}$') { throw 'Cannot identify the source commit.' }
$releaseRoot = Join-Path $outputRoot "v$version"
# Refuse to overwrite a release directory so outputs cannot mix with stale builds.
if (Test-Path -LiteralPath $releaseRoot) { throw "Output already exists: $releaseRoot. Choose a fresh -OutputDirectory." }
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
$buildRoot = Join-Path $outputRoot ('build-' + [Guid]::NewGuid().ToString('N'))
$verificationRoot = Join-Path $releaseRoot 'verification'
New-Item -ItemType Directory -Path $verificationRoot -Force | Out-Null

function Invoke-Dotnet([string[]]$Arguments) {
    & $dotnetExecutable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $($Arguments -join ' ')" }
}

function Invoke-OfflineApp([string]$Executable, [string[]]$Arguments, [string]$RuntimeRoot) {
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $Executable
    $info.WorkingDirectory = Split-Path -Parent $Executable
    $info.Arguments = ($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.RedirectStandardError = $true
    $info.RedirectStandardOutput = $true
    $info.EnvironmentVariables['DOTNET_DISABLE_GUI_ERRORS'] = '1'
    $info.EnvironmentVariables['DOTNET_ROOT'] = $RuntimeRoot
    $info.EnvironmentVariables['DOTNET_ROOT_X64'] = $RuntimeRoot
    $info.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $info
    try {
        if (-not $process.Start()) { throw 'App did not start.' }
        $stderr = $process.StandardError.ReadToEndAsync()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) { $process.Kill(); $process.WaitForExit(); throw 'Offline app check timed out.' }
        [pscustomobject]@{ ExitCode = $process.ExitCode; Stderr = $stderr.Result; Stdout = $stdout.Result }
    }
    finally { $process.Dispose() }
}

[xml]$portableProfile = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\Moondrop.Wpf\Properties\PublishProfiles\Portable.pubxml') -Raw
$coreVersion = $portableProfile.Project.PropertyGroup.RuntimeFrameworkVersion
$coreOnlyRoot = Join-Path $verificationRoot 'core-only-runtime'
New-Item -ItemType Directory -Path "$coreOnlyRoot\host\fxr", "$coreOnlyRoot\shared\Microsoft.NETCore.App" -Force | Out-Null
Copy-Item -LiteralPath "$dotnetRoot\host\fxr\$coreVersion" -Destination "$coreOnlyRoot\host\fxr" -Recurse
Copy-Item -LiteralPath "$dotnetRoot\shared\Microsoft.NETCore.App\$coreVersion" -Destination "$coreOnlyRoot\shared\Microsoft.NETCore.App" -Recurse

$assets = @()
foreach ($variant in @('Portable', 'Slim')) {
    # Use separate pristine bin/obj trees; switching profiles in one tree can keep stale runtimeconfig files.
    $stage = Join-Path $buildRoot $variant
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    foreach ($inputFile in (Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src') -Recurse -File)) {
        $relativePath = $inputFile.FullName.Substring($repositoryRoot.Length + 1)
        if ($relativePath -match '[\\/](bin|obj)[\\/]') { continue }
        $destination = Join-Path $stage $relativePath
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $inputFile.FullName -Destination $destination
    }
    foreach ($control in @('global.json', 'NuGet.Config', 'nuget.config', 'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props')) {
        $controlPath = Join-Path $repositoryRoot $control
        if (Test-Path -LiteralPath $controlPath) { Copy-Item -LiteralPath $controlPath -Destination $stage -Force }
    }
    $publishRoot = Join-Path $stage 'publish'
    Push-Location $stage
    try {
        Invoke-Dotnet @('publish', 'src/Moondrop.Wpf/Moondrop.Wpf.csproj', '-c', 'Release', '-r', 'win-x64', '-o', $publishRoot,
            "-p:PublishProfile=$variant", '-p:RestoreLockedMode=true', '-p:DebugType=embedded', "-p:SourceRevisionId=$sourceRevision")
    }
    finally { Pop-Location }
    $publishedFiles = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -File)
    if ($publishedFiles.Count -ne 1 -or $publishedFiles[0].Name -ne 'Moondrop.Wpf.exe') {
        throw "$variant must publish exactly one application EXE. Found: $($publishedFiles.Name -join ', ')"
    }

    $packageName = "Moondrop-v$version-win-x64-$variant"
    $packageRoot = Join-Path $releaseRoot $packageName
    New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
    $exe = Join-Path $packageRoot 'Moondrop.exe'
    Copy-Item -LiteralPath $publishedFiles[0].FullName -Destination $exe
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination $packageRoot
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\licenses') -Destination (Join-Path $packageRoot 'licenses') -Recurse
    $runtimeMessage = if ($variant -eq 'Portable') { "Includes .NET $coreVersion. No separate .NET installation is needed." } else { 'Requires an installed .NET 10 Desktop Runtime x64.' }
    $readme = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'release-README.txt')).Replace('{{VERSION}}', $version).Replace('{{VARIANT}}', $variant).Replace('{{RUNTIME}}', $runtimeMessage)
    [IO.File]::WriteAllText((Join-Path $packageRoot 'README.txt'), $readme, (New-Object Text.UTF8Encoding($false)))

    # Each process uses an isolated workspace and diagnostic modes that disable hardware.
    $workspace = Join-Path $verificationRoot "$variant-workspace"
    $runtimeForCheck = if ($variant -eq 'Portable') { $coreOnlyRoot } else { $dotnetRoot }
    foreach ($smokeStage in @('write', 'read')) {
        $check = Invoke-OfflineApp $exe @("--workspace-smoke=$smokeStage", "--workspace=$workspace") $runtimeForCheck
        if ($check.ExitCode -ne 0) { throw "$variant $smokeStage smoke failed: $($check.Stderr)" }
        $result = Get-Content -LiteralPath (Join-Path $workspace "smoke-$smokeStage-result.json") -Raw | ConvertFrom-Json
        if (-not $result.passed -or $result.hardwareAccess) { throw 'Offline smoke result is invalid.' }
    }
    foreach ($page in @('Eq', 'Presets', 'Settings')) {
        $capture = Join-Path $verificationRoot "$variant-$page.png"
        $check = Invoke-OfflineApp $exe @('--demo', '--theme=dark', "--page=$page", "--screenshot=$capture", '--width=1180', '--height=780') $runtimeForCheck
        if ($check.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $capture)) { throw "$variant $page UI capture failed." }
    }
    if ($variant -eq 'Slim') {
        $missing = Invoke-OfflineApp $exe @('--demo') $coreOnlyRoot
        if ($missing.ExitCode -eq 0 -or $missing.Stderr -notmatch 'Microsoft.WindowsDesktop.App' -or $missing.Stderr -notmatch 'https://aka.ms/dotnet-core-applaunch') {
            throw 'Missing Desktop Runtime guidance was not verified.'
        }
        [IO.File]::WriteAllText((Join-Path $verificationRoot 'Slim-missing-runtime.txt'), $missing.Stderr)
    }
    $packageFiles = @(Get-ChildItem -LiteralPath $packageRoot -Recurse -File)
    if (@($packageFiles | Where-Object { $_.Extension -in @('.dll', '.pdb', '.md') }).Count -ne 0) { throw 'Unexpected development files in package.' }
    $archivePath = Join-Path $releaseRoot "$packageName.zip"
    Compress-Archive -LiteralPath $packageRoot -DestinationPath $archivePath -CompressionLevel Optimal
    $assets += [pscustomobject]@{
        Variant = $variant; File = [IO.Path]::GetFileName($archivePath)
        Bytes = (Get-Item -LiteralPath $archivePath).Length
        ExeBytes = (Get-Item -LiteralPath $exe).Length
        Sha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
        ExeSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
        PackagedFiles = $packageFiles.Count; WorkspaceSmokePassed = $true; UiCapturesPassed = $true
    }
    Write-Host "$variant package and offline checks passed."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Drawing
foreach ($page in @('Eq', 'Presets', 'Settings')) {
    $portableCapture = Join-Path $verificationRoot "Portable-$page.png"
    $slimCapture = Join-Path $verificationRoot "Slim-$page.png"
    if ((Get-FileHash -LiteralPath $portableCapture).Hash -ne (Get-FileHash -LiteralPath $slimCapture).Hash) { throw "$page differs between download variants." }
    $bitmap = New-Object Drawing.Bitmap($portableCapture)
    try {
        if ($bitmap.Width -ne 1180 -or $bitmap.Height -ne 780) { throw 'Unexpected capture dimensions.' }
        $colors = New-Object 'System.Collections.Generic.HashSet[int]'
        for ($y = 0; $y -lt $bitmap.Height; $y += 16) {
            for ($x = 0; $x -lt $bitmap.Width; $x += 16) { [void]$colors.Add($bitmap.GetPixel($x, $y).ToArgb()) }
        }
        if ($colors.Count -lt 20) { throw "$page capture is empty or incomplete." }
    }
    finally { $bitmap.Dispose() }
}
foreach ($asset in $assets) {
    $archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $releaseRoot $asset.File))
    try {
        $entries = @($archive.Entries | Where-Object { $_.Name })
        if ($entries.Count -ne $asset.PackagedFiles) { throw 'ZIP file count mismatch.' }
        $entry = @($entries | Where-Object { $_.Name -eq 'Moondrop.exe' })
        if ($entry.Count -ne 1) { throw 'ZIP application missing or duplicated.' }
        $stream = $entry[0].Open()
        $hasher = [Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
        finally { $hasher.Dispose(); $stream.Dispose() }
        if ($hash -ne $asset.ExeSha256) { throw 'ZIP executable checksum mismatch.' }
    }
    finally { $archive.Dispose() }
}
[IO.File]::WriteAllLines((Join-Path $releaseRoot 'SHA256SUMS.txt'), [string[]]@($assets | ForEach-Object { "$($_.Sha256)  $($_.File)" }), (New-Object Text.UTF8Encoding($false)))
$report = [pscustomobject]@{ Version = $version; SourceRevision = $sourceRevision; Sdk = $sdkVersion; PortableRuntime = $coreVersion; HardwareAccess = $false; Assets = $assets }
[IO.File]::WriteAllText((Join-Path $releaseRoot 'BUILD-VERIFICATION.json'), ($report | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
Write-Host "Release packages ready in $releaseRoot"
