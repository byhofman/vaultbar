# Runs install.ps1 against the published release and a freshly built zip, in throwaway folders. The signed path is
# switched on with VAULTBAR_EXPECT_SIGNER and checked with a self-signed certificate that only this runner trusts.
# Usage: tests/install-test-windows.ps1 dist/vaultbar-win-x64.zip
param([Parameter(Mandatory)] [string]$Zip)
$ErrorActionPreference = 'Stop'
$installer = Join-Path $PSScriptRoot '..\install\install.ps1'
$Zip = (Resolve-Path $Zip).Path
$sandbox = Join-Path $env:RUNNER_TEMP ("install-test-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $sandbox | Out-Null
$dir = Join-Path $sandbox 'vaultbar'
$options = @{ InstallDir = $dir; NoAutostart = $true; NoPath = $true; NoStart = $true; NoShortcut = $true }
$failures = 0

function Check([string]$Name, [scriptblock]$Test) {
    try {
        if (& $Test) { Write-Host "PASS  $Name" } else { Write-Host "FAIL  $Name"; $script:failures++ }
    }
    catch { Write-Host "FAIL  $Name"; Write-Host "      $_"; $script:failures++ }
}

function Refused([hashtable]$Extra) {
    try { & $installer @options @Extra *> $null; return $false } catch { return $true }
}

function Clean { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }

$hash = (Get-FileHash $Zip).Hash
Check 'the published release downloads, is verified and installs' { & $installer @options | Out-Host; Test-Path "$dir\vaultbar.exe" }
Clean
Check 'a wrong checksum is refused' { Refused @{ Archive = $Zip; Sha256 = ('0' * 64) } }
Check 'nothing is installed after a refused checksum' { -not (Test-Path "$dir\vaultbar.exe") }
Check 'the built zip installs with its checksum' { & $installer @options -Archive $Zip -Sha256 $hash | Out-Host; Test-Path "$dir\vaultbar.exe" }
Clean

$env:VAULTBAR_EXPECT_SIGNER = 'CN=Vaultbar Test Signer'
Check 'with a signer set, an unsigned build is refused' { Refused @{ Archive = $Zip; Sha256 = $hash } }
Check 'nothing is installed after a refused signature' { -not (Test-Path "$dir\vaultbar.exe") }

$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=Vaultbar Test Signer' -CertStoreLocation Cert:\CurrentUser\My
$stores = foreach ($name in 'Root', 'TrustedPublisher') {
    $store = [Security.Cryptography.X509Certificates.X509Store]::new($name, 'LocalMachine')
    $store.Open('ReadWrite')
    $store.Add($cert)
    $store
}
try {
    $signedDir = Join-Path $sandbox 'signed'
    New-Item -ItemType Directory $signedDir | Out-Null
    Expand-Archive $Zip -DestinationPath $signedDir
    $null = Set-AuthenticodeSignature "$signedDir\vaultbar.exe" -Certificate $cert -HashAlgorithm SHA256
    $signedZip = Join-Path $sandbox 'vaultbar-signed.zip'
    Compress-Archive "$signedDir\vaultbar.exe" $signedZip
    Check 'a build signed by the expected subject installs' { & $installer @options -Archive $signedZip | Out-Host; Test-Path "$dir\vaultbar.exe" }
    Clean
    $env:VAULTBAR_EXPECT_SIGNER = 'CN=Fabrikam Release Signing'
    Check 'a validly signed build from another subject is refused' { Refused @{ Archive = $signedZip } }
}
finally {
    Remove-Item Env:\VAULTBAR_EXPECT_SIGNER
    foreach ($store in $stores) { $store.Remove($cert); $store.Close() }
    Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)"
}

& $installer -InstallDir $dir -Uninstall | Out-Null
Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "$failures failed"
if ($failures) { exit 1 }
