<#
.SYNOPSIS
    Builds Clipline's release packages into artifacts\.

.DESCRIPTION
    Targets:
      installer  Clipline-<version>-<arch>-setup.exe     Inno Setup installer; installs the
                                                         Windows App Runtime when it is missing
      portable   Clipline-<version>-<arch>-portable.zip  unzip and run: carries the Windows App
                                                         Runtime, settings kept beside the exe
      msix       Clipline-<version>.msixbundle           for the Microsoft Store, unsigned (the
                                                         Store signs it); a single
                                                         Clipline-<version>-<arch>.msix when
                                                         built for one architecture
      exe        installer and portable
      all        all three (the default)

    The version comes from Directory.Build.props. The same script runs in
    GitHub Actions, so what builds here builds there.

    Needs the .NET 10 SDK, and Inno Setup 6 for the installer
    (winget install JRSoftware.InnoSetup).

.EXAMPLE
    pwsh build/build.ps1
.EXAMPLE
    pwsh build/build.ps1 -Target exe -Arch x64,arm64
.EXAMPLE
    pwsh build/build.ps1 -Target msix -Arch x64,arm64 -MsixIdentityName 12345Publisher.Clipline -MsixPublisher CN=XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX -MsixPublisherDisplayName Publisher
#>
[CmdletBinding()]
param(
    # all, exe, installer, portable, msix; several as a list or "exe,msix".
    [string[]] $Target = @('all'),

    # x64, arm64; both as "x64,arm64".
    [string[]] $Arch = @('x64'),

    [string] $OutputDir,

    # The MSIX's identity, as Partner Center shows it under Product
    # management → Product identity: Package/Identity/Name,
    # Package/Identity/Publisher ("CN=<GUID>") and
    # Package/Properties/PublisherDisplayName.
    [string] $MsixIdentityName = $env:MSIX_IDENTITY_NAME,
    [string] $MsixPublisher = $env:MSIX_PUBLISHER,
    [string] $MsixPublisherDisplayName = $env:MSIX_PUBLISHER_DISPLAY_NAME,

    # Shown in the app's about section and the installer.
    [string] $RepositoryUrl = 'https://github.com/AlphaBrock/clipline'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\Clipline\Clipline.csproj'
if (-not $OutputDir) { $OutputDir = Join-Path $root 'artifacts' }
$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)

# "pwsh -File" hands "x64,arm64" over as one string.
function Split-List([string[]] $values, [string[]] $allowed, [string] $name) {
    $list = @($values -split ',' | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ })
    foreach ($value in $list) {
        if ($value -notin $allowed) { throw "Unknown -${name} '$value'. Use one or more of: $($allowed -join ', ')" }
    }
    $list
}
$Target = Split-List $Target @('all', 'exe', 'installer', 'portable', 'msix') 'Target'
$Arch = Split-List $Arch @('x64', 'arm64') 'Arch'

$targets = [System.Collections.Generic.HashSet[string]]::new()
foreach ($t in $Target) {
    switch ($t) {
        'all' { 'installer', 'portable', 'msix' | ForEach-Object { [void]$targets.Add($_) } }
        'exe' { 'installer', 'portable' | ForEach-Object { [void]$targets.Add($_) } }
        default { [void]$targets.Add($t) }
    }
}

function Step([string] $text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

function Invoke-Native([string] $exe, [string[]] $arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$([System.IO.Path]::GetFileName($exe)) failed with exit code $LASTEXITCODE" }
}

# MARK: Version, changelog, repository

. (Join-Path $PSScriptRoot 'version.ps1')
$version = Get-CliplineVersion
Step "Clipline $version  ($($targets -join ', ') for $($Arch -join ', '))"

if (-not (Get-ReleaseNotes -Version $version)) {
    Write-Warning "CHANGELOG.md has no section for $version. The release workflow will refuse to publish it."
}

$common = @('-c', 'Release', '-p:SelfContained=true', '-p:DebugType=none', '-nologo')
if ($RepositoryUrl) { $common += "-p:RepositoryUrl=$RepositoryUrl" }

New-Item -ItemType Directory -Force $OutputDir | Out-Null
$work = Join-Path $OutputDir '.work'
$made = @()

# MARK: Inno Setup

function Find-Iscc {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    foreach ($dir in "$env:LOCALAPPDATA\Programs\Inno Setup 6", "${env:ProgramFiles(x86)}\Inno Setup 6", "$env:ProgramFiles\Inno Setup 6") {
        $exe = Join-Path $dir 'ISCC.exe'
        if (Test-Path $exe) { return $exe }
    }
    throw 'Inno Setup 6 was not found. Install it with: winget install JRSoftware.InnoSetup'
}

# MARK: Microsoft Store

function Find-SdkTool([string] $name) {
    # The Windows SDK build tools package the project already restores.
    $packages = (dotnet nuget locals global-packages --list) -replace '^global-packages:\s*', ''
    $arch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
    $exe = Get-ChildItem (Join-Path $packages.Trim() 'microsoft.windows.sdk.buildtools') -Recurse -Filter $name -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -eq $arch } | Sort-Object FullName -Descending | Select-Object -First 1
    if ($exe) { return $exe.FullName }
    $exe = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter $name -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -eq $arch } | Sort-Object FullName -Descending | Select-Object -First 1
    if ($exe) { return $exe.FullName }
    throw "$name was not found. It comes with the Microsoft.Windows.SDK.BuildTools package; run dotnet restore first."
}

# Nothing is signed: the Store signs the package with the identity it
# reserved for the app, so the package has to carry exactly that identity.
$identity = @()
if ($targets.Contains('msix')) {
    if ($MsixIdentityName -and $MsixPublisher -and $MsixPublisherDisplayName) {
        if (-not $MsixPublisher.Trim().StartsWith('CN=')) {
            throw "-MsixPublisher is Partner Center's Package/Identity/Publisher, such as CN=XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX; got '$MsixPublisher'."
        }
        $identity = @("-p:MsixIdentityName=$($MsixIdentityName.Trim())", "-p:MsixPublisher=$($MsixPublisher.Trim())",
            "-p:MsixPublisherDisplayName=$($MsixPublisherDisplayName.Trim())")
        Write-Host "MSIX identity: $($MsixIdentityName.Trim()), $($MsixPublisher.Trim())"
    }
    else {
        Write-Warning 'No Partner Center identity given (-MsixIdentityName, -MsixPublisher, -MsixPublisherDisplayName): the MSIX gets placeholders, which the Store will not accept.'
    }
}
$msixFiles = @()

# MARK: Build

foreach ($a in $Arch) {
    $rid = "win-$a"
    $platform = if ($a -eq 'arm64') { 'ARM64' } else { 'x64' }

    if ($targets.Contains('portable')) {
        # Carries the Windows App Runtime, so it runs on a computer that has
        # never had it, with nothing to install.
        Step "Publishing the portable copy for $a"
        $stage = Join-Path $work "portable-$a"
        if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
        $folder = Join-Path $stage 'Clipline'
        Invoke-Native dotnet (@('publish', $project, '-r', $rid, "-p:Platform=$platform",
            '-p:WindowsAppSDKSelfContained=true', '-o', $folder) + $common)
        # Its presence is what makes this copy portable: see AppInfo.cs.
        Set-Content -Encoding utf8 (Join-Path $folder 'portable.txt') @(
            'Clipline 便携版：设置、日志和剪贴板截图都保存在旁边的 data 文件夹里。'
            '删除本文件后，它会像安装版一样把数据保存在 %LOCALAPPDATA%\Clipline。')
        $zip = Join-Path $OutputDir "Clipline-$version-$a-portable.zip"
        if (Test-Path $zip) { Remove-Item $zip }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory($folder, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)
        $made += $zip
    }

    if ($targets.Contains('installer')) {
        Step "Publishing the exe for $a"
        $publish = Join-Path $work "publish-$a"
        if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
        Invoke-Native dotnet (@('publish', $project, '-r', $rid, "-p:Platform=$platform", '-o', $publish) + $common)

        Step "Compiling the installer for $a"
        $iss = Join-Path $PSScriptRoot 'installer\Clipline.iss'
        $defines = @("/DAppVersion=$version", "/DArch=$a", "/DSourceDir=$publish", "/DOutputDir=$OutputDir", '/Q')
        if ($RepositoryUrl) { $defines += "/DRepositoryUrl=$RepositoryUrl" }
        # The Windows App Runtime the installer checks for, and fetches
        # when it is missing.
        $runtime = Get-WindowsAppRuntime
        $defines += @("/DRuntimeName=$($runtime.Name)", "/DRuntimeChannel=$($runtime.Channel)",
            "/DRuntimeMinVersion=$($runtime.MinVersion)", "/DRuntimeUrl=$($runtime.DownloadUrl -f $a)")
        # Antivirus scanning the fresh setup.exe sometimes makes ISCC's
        # final resource update fail ("EndUpdateResource failed"); a
        # second try goes through.
        for ($attempt = 1; ; $attempt++) {
            try { Invoke-Native (Find-Iscc) ($defines + $iss); break }
            catch { if ($attempt -ge 3) { throw }; Write-Warning "ISCC failed, trying again ($_)"; Start-Sleep -Seconds 3 }
        }
        $made += Join-Path $OutputDir "Clipline-$version-$a-setup.exe"
    }

    if ($targets.Contains('msix')) {
        Step "Packaging the MSIX for $a"
        $packageDir = Join-Path $work "msix-$a"
        if (Test-Path $packageDir) { Remove-Item -Recurse -Force $packageDir }
        $msixArgs = @('publish', $project, '-r', $rid, "-p:Platform=$platform",
            '-p:WindowsPackageType=MSIX', '-p:GenerateAppxPackageOnBuild=true',
            "-p:AppxPackageDir=$packageDir\")
        Invoke-Native dotnet ($msixArgs + $identity + $common)
        $msix = Get-ChildItem $packageDir -Recurse -Filter '*.msix' | Select-Object -First 1
        if (-not $msix) { throw "No .msix came out of the build in $packageDir" }
        $destination = Join-Path $work "Clipline-$version-$a.msix"
        Copy-Item $msix.FullName $destination -Force
        $msixFiles += $destination
    }
}

# One bundle for every architecture, so a Store submission is a single
# upload. Once a product has taken a bundle, the Store wants bundles from
# then on, so release builds always make one.
if ($msixFiles.Count -eq 1) {
    $destination = Join-Path $OutputDir ([System.IO.Path]::GetFileName($msixFiles[0]))
    Copy-Item $msixFiles[0] $destination -Force
    $made += $destination
}
elseif ($msixFiles.Count -gt 1) {
    Step 'Bundling the MSIX packages'
    $bundleDir = Join-Path $work 'msixbundle'
    if (Test-Path $bundleDir) { Remove-Item -Recurse -Force $bundleDir }
    New-Item -ItemType Directory -Force $bundleDir | Out-Null
    Copy-Item $msixFiles $bundleDir
    $bundle = Join-Path $OutputDir "Clipline-$version.msixbundle"
    # The packages' own version, x.y.z.0.
    $bundleVersion = ($version -split '-')[0] + '.0'
    Invoke-Native (Find-SdkTool 'makeappx.exe') @('bundle', '/d', $bundleDir, '/p', $bundle, '/bv', $bundleVersion, '/o')
    $made += $bundle
}

Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue

Step 'Done'
foreach ($file in $made) {
    $size = (Get-Item $file).Length / 1MB
    Write-Host ('  {0,-48} {1,7:N1} MB' -f [System.IO.Path]::GetFileName($file), $size)
}
