<#
.SYNOPSIS
    Installs (or removes) Vaultbar for the current user. No admin rights, no prompts.
.EXAMPLE
    irm https://raw.githubusercontent.com/byhofman/vaultbar/main/install/install.ps1 | iex
.EXAMPLE
    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/byhofman/vaultbar/main/install/install.ps1))) -Uninstall
#>
param(
    [string]$Repo = 'byhofman/vaultbar',
    [string]$Version = 'latest',
    [string]$Archive,
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
        $base = if ($Version -eq 'latest') { "https://github.com/$Repo/releases/latest/download" } else { "https://github.com/$Repo/releases/download/$Version" }
        $Archive = Join-Path $tmp "vaultbar-$rid.zip"
        Write-Host "Downloading $base/vaultbar-$rid.zip"
        Invoke-WebRequest "$base/vaultbar-$rid.zip" -OutFile $Archive -UseBasicParsing
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
