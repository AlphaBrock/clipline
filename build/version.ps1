# Reads the version and its release notes, for build.ps1 and the release
# workflow. Dot-source it:  . build/version.ps1

$script:CliplineRoot = Split-Path $PSScriptRoot -Parent

# The version in Directory.Build.props: VersionPrefix, plus -VersionSuffix if set.
function Get-CliplineVersion {
    [xml] $props = Get-Content -Raw (Join-Path $script:CliplineRoot 'Directory.Build.props')
    $group = $props.Project.PropertyGroup | Where-Object { $_.VersionPrefix } | Select-Object -First 1
    if (-not $group) { throw 'Directory.Build.props has no VersionPrefix.' }
    $version = $group.VersionPrefix.Trim()
    if ($group.VersionSuffix) { $version += '-' + $group.VersionSuffix.Trim() }
    $version
}

# The Windows App Runtime the app needs, which it does not carry: the
# framework package's name, its release channel ("1.7"), the lowest version
# that will do, and where Microsoft hands out its installer. Read from the
# Microsoft.WindowsAppSDK package the project references, so it follows
# upgrades by itself. The package has to be restored first.
function Get-WindowsAppRuntime {
    [xml] $project = Get-Content -Raw (Join-Path $script:CliplineRoot 'src\Clipline\Clipline.csproj')
    $sdkVersion = ($project.Project.ItemGroup.PackageReference | Where-Object { $_.Include -eq 'Microsoft.WindowsAppSDK' }).Version
    if (-not $sdkVersion) { throw 'Clipline.csproj does not reference Microsoft.WindowsAppSDK.' }
    $channel = ($sdkVersion -split '\.')[0..1] -join '.'

    $packages = ((dotnet nuget locals global-packages --list) -replace '^global-packages:\s*', '').Trim()
    $props = Join-Path $packages "microsoft.windowsappsdk\$sdkVersion\buildTransitive\MicrosoftWindowsAppSDKFoundationAppXVersion.props"
    if (-not (Test-Path $props)) { throw "Microsoft.WindowsAppSDK $sdkVersion is not restored yet: run dotnet restore." }
    [xml] $xml = Get-Content -Raw $props
    [pscustomobject] @{
        Name        = "Microsoft.WindowsAppRuntime.$channel"
        Channel     = $channel
        MinVersion  = $xml.Project.PropertyGroup.MicrosoftWindowsAppSDKFoundationAppXVersion.Trim()
        DownloadUrl = "https://aka.ms/windowsappsdk/$channel/latest/windowsappruntimeinstall-{0}.exe"
    }
}

# The body of "## [<Version>]" in CHANGELOG.md, without its heading, or
# nothing if the changelog has no such section.
function Get-ReleaseNotes {
    param([Parameter(Mandatory)] [string] $Version)
    $lines = Get-Content -Encoding utf8 (Join-Path $script:CliplineRoot 'CHANGELOG.md')
    $heading = '^##\s+\[?' + [regex]::Escape($Version) + '\]?(\s|$)'
    $notes = $null
    foreach ($line in $lines) {
        if ($null -ne $notes) {
            if ($line -match '^##\s') { break }
            $notes += , $line
        }
        elseif ($line -match $heading) { $notes = @() }
    }
    if ($null -eq $notes) { return $null }
    $text = ($notes -join "`n").Trim()
    if ($text) { $text }
}

# What a GitHub release says: the changelog section, then which file to
# download. Fails if the changelog has nothing for this version.
function New-ReleaseNotes {
    param([Parameter(Mandatory)] [string] $Version, [switch] $SelfSigned)
    $notes = Get-ReleaseNotes -Version $Version
    if (-not $notes) { throw "CHANGELOG.md has no section for $Version. Add '## [$Version] - $(Get-Date -Format yyyy-MM-dd)' with its changes." }
    $download = @(
        ''
        '---'
        ''
        '### 下载哪个'
        ''
        '| 文件 | 说明 |'
        '|:--|:--|'
        "| ``Clipline-$Version-x64-setup.exe`` | **安装版**，推荐。默认只为当前用户安装，不需要管理员权限 |"
        "| ``Clipline-$Version-x64-portable.zip`` | **便携版**。解压即用，设置保存在同目录的 ``data`` 文件夹 |"
        "| ``Clipline-$Version-x64.msix`` | **MSIX 包**。由 Windows 管理安装和卸载 |"
        ''
        'ARM64 设备（如骁龙笔记本）请选文件名带 `arm64` 的那一个。'
    )
    $runtime = Get-WindowsAppRuntime
    $download += @(
        ''
        "需要 **Windows App Runtime $($runtime.Channel)**，很多电脑已经装过。安装版会自动下载安装；便携版第一次启动时如果缺少，会弹出 Windows 的提示带你去下载；"
        "也可以手动安装：[x64]($($runtime.DownloadUrl -f 'x64')) · [ARM64]($($runtime.DownloadUrl -f 'arm64'))。"
    )
    if ($SelfSigned) {
        $download += @(
            ''
            'MSIX 用自签名证书签名。第一次安装前，用管理员身份打开 PowerShell 信任附带的 `Clipline.cer`：'
            ''
            '```powershell'
            'Import-Certificate -FilePath .\Clipline.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople'
            '```'
        )
    }
    $download += @('', '`SHA256SUMS.txt` 里是每个文件的 SHA-256 校验值。')
    $notes + "`n" + ($download -join "`n")
}
