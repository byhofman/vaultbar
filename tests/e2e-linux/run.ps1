# End-to-end test of the Linux/X11 build in a disposable Ubuntu container.
# Usage: ./tests/e2e-linux/run.ps1 [-DockerContext linux]
param([string]$DockerContext = 'linux')
$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot/../.."

dotnet publish "$root/src/Vaultbar" -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o "$root/dist/linux-x64" -v q --nologo
$env:VAULTBAR_DEMO_OUT = "$root/dist/e2e-data.json"
dotnet test "$root/tests/Vaultbar.Tests" --filter DemoVault -v q --nologo | Out-Null
Remove-Item Env:VAULTBAR_DEMO_OUT

$mount = if ($IsWindows) { '/mnt/' + $root.Path.Substring(0, 1).ToLower() + $root.Path.Substring(2).Replace('\', '/') } else { $root.Path }
docker --context $DockerContext run --rm -v "${mount}:/w" ubuntu:24.04 bash /w/tests/e2e-linux/in-container.sh
exit $LASTEXITCODE
