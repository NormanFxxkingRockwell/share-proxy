$ErrorActionPreference = 'Stop'
$dir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $dir 'Program.cs'
$out  = Join-Path $dir 'share.exe'

$win = $env:windir
if (-not $win) { $win = $env:SystemRoot }
if (-not $win) { $win = 'C:\Windows' }

# rewrite Program.cs as UTF-8 with BOM so csc decodes CJK correctly
$text = [IO.File]::ReadAllText($src, (New-Object System.Text.UTF8Encoding($false)))
[IO.File]::WriteAllText($src, $text, (New-Object System.Text.UTF8Encoding($true)))

$csc = Join-Path $win 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc not found: $csc" }

& $csc /nologo /target:winexe /platform:anycpu /win32icon:"$(Join-Path $dir 'app.ico')" /out:"$out" /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll "$src"
if ($LASTEXITCODE -ne 0) { throw "compile failed (exit $LASTEXITCODE)" }
"BUILD OK -> $out ($((Get-Item $out).Length) bytes)"
