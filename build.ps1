# Builds self-contained vaultbar binaries (no .NET runtime or other dependencies needed on the target).
# Usage: ./build.ps1 [-Runtime win-x64,linux-x64,...] [-Version 1.2.3]
# macOS builds include a Swift library for Touch ID, so they only build on a Mac.
param(
    [string[]]$Runtime = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64') + $(if ($IsMacOS) { @('osx-arm64', 'osx-x64') } else { @() }),
    [string]$Version
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
# The tag is the version: CI passes it, a local build is 0.0.0.
if (-not $Version) { $Version = '0.0.0' }

function New-Icns([string]$Path) {
    $chunks = [ordered]@{ ic11 = 32; ic12 = 64; ic07 = 128; ic13 = 256; ic08 = 256; ic14 = 512; ic09 = 512; ic10 = 1024 }
    $body = [IO.MemoryStream]::new()
    foreach ($type in $chunks.Keys) {
        $png = [IO.File]::ReadAllBytes("$root/branding/app-icon/png/icon-$($chunks[$type]).png")
        $body.Write([Text.Encoding]::ASCII.GetBytes($type))
        $body.Write([BitConverter]::GetBytes([Net.IPAddress]::HostToNetworkOrder([int]($png.Length + 8))))
        $body.Write($png)
    }
    $file = [IO.File]::Create($Path)
    try {
        $file.Write([Text.Encoding]::ASCII.GetBytes('icns'))
        $file.Write([BitConverter]::GetBytes([Net.IPAddress]::HostToNetworkOrder([int]($body.Length + 8))))
        $body.WriteTo($file)
    }
    finally { $file.Dispose() }
}

foreach ($rid in $Runtime) {
    $out = Join-Path $dist $rid
    Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "==> $rid" -ForegroundColor Cyan

    if ($rid -like 'osx-*') {
        $app = Join-Path $out 'Vaultbar.app'
        dotnet publish "$root/src/Vaultbar" -c Release -r $rid --self-contained -p:PublishSingleFile=false -o "$app/Contents/MacOS" -p:Version=$Version -v q --nologo
        if ($LASTEXITCODE) { throw "publish $rid failed" }
        & "$root/src/Vaultbar.TouchId/build.sh" ($rid -eq 'osx-arm64' ? 'arm64' : 'x86_64') "$app/Contents/MacOS"
        if ($LASTEXITCODE) { throw "Touch ID library for $rid failed" }
        New-Item -ItemType Directory -Force "$app/Contents/Resources" | Out-Null
        New-Icns "$app/Contents/Resources/Vaultbar.icns"
        @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Vaultbar</string>
  <key>CFBundleDisplayName</key><string>Vaultbar</string>
  <key>CFBundleIdentifier</key><string>dev.byhofman.vaultbar</string>
  <key>CFBundleExecutable</key><string>vaultbar</string>
  <key>CFBundleIconFile</key><string>Vaultbar</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleVersion</key><string>$Version</string>
  <key>CFBundleShortVersionString</key><string>$Version</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>LSUIElement</key><true/>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
"@ | Set-Content -Path "$app/Contents/Info.plist" -Encoding utf8NoBOM
        tar -czf "$dist/vaultbar-$rid.tar.gz" -C $out Vaultbar.app
    }
    else {
        dotnet publish "$root/src/Vaultbar" -c Release -r $rid --self-contained -p:PublishSingleFile=true -o $out -p:Version=$Version -v q --nologo
        if ($LASTEXITCODE) { throw "publish $rid failed" }
        if ($rid -like 'win-*') { Compress-Archive -Path "$out/vaultbar.exe" -DestinationPath "$dist/vaultbar-$rid.zip" -Force }
        else { tar -czf "$dist/vaultbar-$rid.tar.gz" -C $out vaultbar }
    }
}

Get-ChildItem $dist -File | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
