# Publish helper: push to your GitHub repo + release checklist
# Usage:  powershell -ExecutionPolicy Bypass -File publish.ps1 -User <your-github-username>
param(
  [Parameter(Mandatory = $true)][string]$User,
  [string]$Repo = 'proxy-hotspot-share'
)
$ErrorActionPreference = 'Stop'
$branch = git branch --show-current
if ($branch -ne 'main') { git branch -M main }
$origin = "https://github.com/$User/$Repo.git"
git remote remove origin 2>$null
git remote add origin $origin
git push -u origin main
""
"=== PUSHED OK -> $origin ==="
"Next (create a Release so people can download the exe):"
"  1. open https://github.com/$User/$Repo/releases/new"
"  2. tag: v1.0.0   title: v1.0.0"
"  3. drag ProxyHotspotShare.exe into the attachments"
"  4. publish"
