# End-to-end test of the Linux/X11 build in a disposable container (the image pinned in tests/linux-image).
# Usage: ./tests/e2e-linux/run.ps1 [-DockerContext linux]
param([string]$DockerContext)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot/../.."
$image = (Get-Content "$root/tests/linux-image" -Raw).Trim()

dotnet publish "$root/src/Vaultbar" -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o "$root/dist/linux-x64" -v q --nologo
$env:VAULTBAR_DEMO_OUT = "$root/dist/e2e-data.json"
dotnet test "$root/tests/Vaultbar.Tests" --filter DemoVault -v q --nologo | Out-Null
Remove-Item Env:VAULTBAR_DEMO_OUT

$mount = if ($IsWindows) { '/mnt/' + $root.Path.Substring(0, 1).ToLower() + $root.Path.Substring(2).Replace('\', '/') } else { $root.Path }
$context = if ($DockerContext) { @('--context', $DockerContext) } else { @() }
docker @context run --rm -v "${mount}:/w" $image bash /w/tests/e2e-linux/in-container.sh
exit $LASTEXITCODE
