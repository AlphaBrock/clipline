<#
.SYNOPSIS
    Builds Clipline's release packages into artifacts\.

.DESCRIPTION
    Targets:
      installer  Clipline-<version>-<arch>-setup.exe     Inno Setup installer
      portable   Clipline-<version>-<arch>-portable.zip  unzip and run, settings kept beside the exe
      msix       Clipline-<version>-<arch>.msix          signed when a certificate is given
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
    pwsh build/build.ps1 -Target msix -CertificatePath build/certs/Clipline.pfx -CertificatePassword ****
#>
[CmdletBinding()]
param(
    # all, exe, installer, portable, msix; several as a list or "exe,msix".
    [string[]] $Target = @('all'),

    # x64, arm64; both as "x64,arm64".
    [string[]] $Arch = @('x64'),

    [string] $OutputDir,

    # A .pfx to sign the MSIX with. Its subject becomes the package's
    # Publisher. Without one the MSIX is built unsigned.
    [string] $CertificatePath = $env:MSIX_CERTIFICATE_PATH,
    [string] $CertificatePassword = $env:MSIX_CERTIFICATE_PASSWORD,
    # A RFC 3161 timestamp server, so the signature outlives the certificate.
    [string] $TimestampUrl = $env:MSIX_TIMESTAMP_URL,

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

# MARK: Signing

function Find-SignTool {
    # The Windows SDK build tools package the project already restores.
    $packages = (dotnet nuget locals global-packages --list) -replace '^global-packages:\s*', ''
    $arch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
    $exe = Get-ChildItem (Join-Path $packages.Trim() 'microsoft.windows.sdk.buildtools') -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -eq $arch } | Sort-Object FullName -Descending | Select-Object -First 1
    if ($exe) { return $exe.FullName }
    $exe = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -eq $arch } | Sort-Object FullName -Descending | Select-Object -First 1
    if ($exe) { return $exe.FullName }
    throw 'signtool.exe was not found. It comes with the Microsoft.Windows.SDK.BuildTools package; run dotnet restore first.'
}

$certificate = $null
if ($targets.Contains('msix') -and $CertificatePath) {
    $CertificatePath = [System.IO.Path]::GetFullPath($CertificatePath)
    $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $CertificatePath, $CertificatePassword,
        [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    Write-Host "Signing the MSIX as $($certificate.Subject)"
}

# MARK: Build

foreach ($a in $Arch) {
    $rid = "win-$a"
    $platform = if ($a -eq 'arm64') { 'ARM64' } else { 'x64' }

    if ($targets.Contains('installer') -or $targets.Contains('portable')) {
        Step "Publishing the exe for $a"
        $publish = Join-Path $work "publish-$a"
        if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
        Invoke-Native dotnet (@('publish', $project, '-r', $rid, "-p:Platform=$platform", '-o', $publish) + $common)

        if ($targets.Contains('portable')) {
            Step "Zipping the portable copy for $a"
            $stage = Join-Path $work "portable-$a"
            if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
            $folder = Join-Path $stage 'Clipline'
            New-Item -ItemType Directory -Force $folder | Out-Null
            Copy-Item -Recurse (Join-Path $publish '*') $folder
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
    }

    if ($targets.Contains('msix')) {
        Step "Packaging the MSIX for $a"
        $packageDir = Join-Path $work "msix-$a"
        if (Test-Path $packageDir) { Remove-Item -Recurse -Force $packageDir }
        $msixArgs = @('publish', $project, '-r', $rid, "-p:Platform=$platform",
            '-p:WindowsPackageType=MSIX', '-p:GenerateAppxPackageOnBuild=true',
            "-p:AppxPackageDir=$packageDir\")
        # The package's Publisher has to be the certificate's subject.
        if ($certificate) { $msixArgs += "-p:MsixPublisher=$($certificate.Subject)" }
        Invoke-Native dotnet ($msixArgs + $common)
        $msix = Get-ChildItem $packageDir -Recurse -Filter '*.msix' | Select-Object -First 1
        if (-not $msix) { throw "No .msix came out of the build in $packageDir" }
        $destination = Join-Path $OutputDir "Clipline-$version-$a.msix"
        Copy-Item $msix.FullName $destination -Force
        $made += $destination
        if ($certificate) {
            # Signed here rather than by MSBuild, whose signing task cannot
            # open a password-protected .pfx when run from the dotnet CLI.
            $sign = @('sign', '/fd', 'SHA256', '/f', $CertificatePath, '/p', $CertificatePassword)
            if ($TimestampUrl) { $sign += @('/tr', $TimestampUrl, '/td', 'SHA256') }
            Invoke-Native (Find-SignTool) ($sign + $destination)
        }
        else {
            Write-Warning 'The MSIX is unsigned, so Windows will not install it. Sign it with -CertificatePath (see build/new-certificate.ps1).'
        }
    }
}

# A self-signed certificate is not trusted anywhere yet; ship its public
# half so users can trust it before installing the MSIX.
if ($certificate -and $certificate.Subject -eq $certificate.Issuer) {
    $cer = Join-Path $OutputDir 'Clipline.cer'
    [System.IO.File]::WriteAllBytes($cer, $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    $made += $cer
}

Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue

Step 'Done'
foreach ($file in $made) {
    $size = (Get-Item $file).Length / 1MB
    Write-Host ('  {0,-48} {1,7:N1} MB' -f [System.IO.Path]::GetFileName($file), $size)
}
