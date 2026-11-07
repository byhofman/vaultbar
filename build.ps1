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
[xml]$proj = Get-Content "$root/src/Vaultbar/Vaultbar.csproj"
if (-not $Version) { $Version = $proj.Project.PropertyGroup.Version | Select-Object -First 1 }

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
        @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Vaultbar</string>
  <key>CFBundleDisplayName</key><string>Vaultbar</string>
  <key>CFBundleIdentifier</key><string>local.vaultbar</string>
  <key>CFBundleExecutable</key><string>vaultbar</string>
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
