<#
.SYNOPSIS
    Installs (or removes) Vaultbar for the current user. No admin rights, no prompts.
.EXAMPLE
    irm https://raw.githubusercontent.com/byhofman/vaultbar/main/install/install.ps1 | iex
.EXAMPLE
    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/byhofman/vaultbar/main/install/install.ps1))) -Uninstall
.NOTES
    A download is checked against the SHA-256 digest GitHub records for the release asset, and refused if it differs.
    With -Archive, pass -Sha256 to check a file you downloaded yourself.
#>
param(
    [string]$Repo = 'byhofman/vaultbar',
    [string]$Version = 'latest',
    [string]$Archive,
    [string]$Sha256,
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\Vaultbar'),
    [switch]$Uninstall,
    [switch]$NoAutostart,
    [switch]$NoPath,
    [switch]$NoStart,
    [switch]$NoShortcut
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$exe = Join-Path $InstallDir 'vaultbar.exe'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Vaultbar.lnk'

function Stop-Vaultbar {
    if (Test-Path $exe) { Start-Process $exe -ArgumentList quit -Wait -WindowStyle Hidden -ErrorAction SilentlyContinue }
    Get-Process vaultbar -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
}

function Set-UserPath([bool]$add) {
    # Edit the raw registry value: [Environment]::SetEnvironmentVariable would expand %VAR% entries and drop REG_EXPAND_SZ.
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
    try {
        $raw = [string]$key.GetValue('Path', '', 'DoNotExpandEnvironmentNames')
        $parts = @($raw -split ';' | Where-Object { $_ -and $_ -ne $InstallDir })
        if ($add) { $parts += $InstallDir }
        $new = $parts -join ';'
        if ($new -eq $raw.TrimEnd(';')) { return }
        $key.SetValue('Path', $new, 'ExpandString')
    }
    finally { $key.Dispose() }
    Add-Type -Namespace Vaultbar -Name Env -MemberDefinition '[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, UIntPtr w, string l, uint flags, uint timeout, out UIntPtr result);'
    $result = [UIntPtr]::Zero
    [void][Vaultbar.Env]::SendMessageTimeout([IntPtr]0xffff, 0x1A, [UIntPtr]::Zero, 'Environment', 2, 2000, [ref]$result)
}

if ($Uninstall) {
    Stop-Vaultbar
    if (Test-Path $exe) { Start-Process $exe -ArgumentList 'autostart', 'off' -Wait -WindowStyle Hidden }
    Set-UserPath $false
    Remove-Item $shortcut, $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'Vaultbar removed. Settings remain in' (Join-Path $env:APPDATA 'vaultbar')
    return
}

$rid = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("vaultbar-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
try {
    if (-not $Archive) {
        $asset = "vaultbar-$rid.zip"
        $api = if ($Version -eq 'latest') { "https://api.github.com/repos/$Repo/releases/latest" } else { "https://api.github.com/repos/$Repo/releases/tags/$Version" }
        try { $release = Invoke-RestMethod $api -UseBasicParsing }
        catch { throw "Could not read the release from GitHub ($api). Try again later, or download it and use -Archive." }
        $digest = ($release.assets | Where-Object name -EQ $asset | Select-Object -First 1).digest
        if ($digest -notmatch '^sha256:([0-9a-f]{64})$') { throw "Release $Version has no $asset with a SHA-256 digest." }
        $Sha256 = $Matches[1]
        $Archive = Join-Path $tmp $asset
        Write-Host "Downloading $asset ($($release.tag_name))"
        Invoke-WebRequest "https://github.com/$Repo/releases/download/$($release.tag_name)/$asset" -OutFile $Archive -UseBasicParsing
    }
    if ($Sha256) {
        $actual = (Get-FileHash $Archive -Algorithm SHA256).Hash
        if ($actual -ne $Sha256) { throw "SHA-256 mismatch for ${Archive}: expected $Sha256, got $($actual.ToLowerInvariant()). Nothing was installed." }
    }
    Expand-Archive $Archive -DestinationPath "$tmp\x" -Force

    Stop-Vaultbar
    New-Item -ItemType Directory $InstallDir -Force | Out-Null
    Copy-Item "$tmp\x\vaultbar.exe" $exe -Force
    Unblock-File $exe

    if (-not $NoShortcut) {
        $lnk = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
        $lnk.TargetPath = $exe
        $lnk.Description = 'Quick search for Bitwarden'
        $lnk.Save()
    }

    if (-not $NoPath) { Set-UserPath $true }
    if (-not $NoAutostart) { Start-Process $exe -ArgumentList 'autostart', 'on' -Wait -WindowStyle Hidden }
    if (-not $NoStart) { Start-Process $exe -ArgumentList background }

    Write-Host "Vaultbar installed to $InstallDir. Press Ctrl+Shift+Space."
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
