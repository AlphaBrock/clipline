<#
.SYNOPSIS
    Makes a self-signed code signing certificate for the MSIX.

.DESCRIPTION
    Writes build/certs/Clipline.pfx (the private key, password protected;
    *.pfx is ignored by git) and build/certs/Clipline.cer (the public half
    users trust before installing). Prints the .pfx as base64, ready to paste
    into the MSIX_CERTIFICATE secret of the GitHub repository; the password
    goes into MSIX_CERTIFICATE_PASSWORD.

    Make it once and keep it: every release must be signed with the same
    certificate, or users have to trust a new one each time. The subject
    becomes the package's Publisher, so changing it makes Windows treat
    the package as a different app.

.EXAMPLE
    pwsh build/new-certificate.ps1 -Password (Read-Host -AsSecureString)
#>
param(
    [Parameter(Mandatory)] [securestring] $Password,
    [string] $Subject = 'CN=Clipline',
    [int] $Years = 5
)

$ErrorActionPreference = 'Stop'
$dir = Join-Path $PSScriptRoot 'certs'
New-Item -ItemType Directory -Force $dir | Out-Null

$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject -FriendlyName 'Clipline MSIX signing' `
    -KeyUsage DigitalSignature -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
    -NotAfter (Get-Date).AddYears($Years) -CertStoreLocation Cert:\CurrentUser\My `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
try {
    $pfx = Join-Path $dir 'Clipline.pfx'
    $cer = Join-Path $dir 'Clipline.cer'
    Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $Password | Out-Null
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
}
finally {
    # The files are the copy that matters; leave the store as it was.
    Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)"
}

Write-Host "Wrote $pfx and $cer for $Subject, valid until $($cert.NotAfter.ToString('yyyy-MM-dd'))."
Write-Host "`nMSIX_CERTIFICATE secret (base64 of the .pfx):`n"
[Convert]::ToBase64String([System.IO.File]::ReadAllBytes($pfx))
