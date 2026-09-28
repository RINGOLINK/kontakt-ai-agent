# Build clean dist/ (ASCII only -- PowerShell 5.1 reads UTF-8-no-BOM as GBK)
$ErrorActionPreference = 'Stop'
$root = 'F:\Deepseek Harness\Kontkat Library'
$rel  = Join-Path $root 'src\KontaktLibManager\bin\Release\net9.0-windows'
$dist = Join-Path $root 'dist'

if (-not (Test-Path $rel)) { throw "Release output not found: $rel (run dotnet build -c Release first)" }

Write-Host '[1] clean old dist'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $dist -Force | Out-Null

Write-Host '[2] copy runtime files'
Get-ChildItem $rel -File | ForEach-Object { Copy-Item $_.FullName -Destination $dist -Force }
$keep = @('.exe', '.dll', '.json')
Get-ChildItem $dist -File | Where-Object { $keep -notcontains $_.Extension.ToLower() } | ForEach-Object {
    Remove-Item $_.FullName -Force
}

Write-Host '[3] copy wwwroot'
Copy-Item (Join-Path $rel 'wwwroot') -Destination (Join-Path $dist 'wwwroot') -Recurse -Force

Write-Host '[4] copy runtimes'
if (Test-Path (Join-Path $rel 'runtimes')) {
    Copy-Item (Join-Path $rel 'runtimes') -Destination (Join-Path $dist 'runtimes') -Recurse -Force
}

Write-Host '[4b] trim runtimes to win-x64 only (others are macOS/Linux/ARM -- useless here)'
$rt = Join-Path $dist 'runtimes'
if (Test-Path $rt) {
    Get-ChildItem $rt -Directory | Where-Object { $_.Name -ne 'win-x64' } | ForEach-Object {
        Remove-Item $_.FullName -Recurse -Force
    }
    Write-Host '    TRIMMED runtimes: win-x64 kept'
}

Write-Host '[5] data/: kspc.exe only'
$distData = Join-Path $dist 'data'
New-Item -ItemType Directory -Path $distData -Force | Out-Null
$kspc = Join-Path $rel 'data\kspc.exe'
if (Test-Path $kspc) {
    Copy-Item $kspc -Destination (Join-Path $distData 'kspc.exe') -Force
    $mb = [math]::Round((Get-Item $kspc).Length / 1MB, 1)
    Write-Host ("    OK kspc.exe " + $mb + " MB")
} else {
    Write-Host '    WARN data\kspc.exe not found'
}

Write-Host '[6] Launch.bat'
$nl = [char]13 + [char]10
$lines = @('@echo off', 'cd /d "%~dp0"', 'start "" "KontaktLibManager.exe"')
$launch = [string]::Join($nl, $lines)
[System.IO.File]::WriteAllText((Join-Path $dist 'Launch.bat'), $launch, [System.Text.ASCIIEncoding]::new())

Write-Host '[7] docs and license'
foreach ($d in @('INSTALL.md', 'README.md', 'LICENSE')) {
    $src = Join-Path $root $d
    if (Test-Path $src) { Copy-Item $src -Destination $dist -Force; Write-Host ("    OK " + $d) }
    else { Write-Host ("    PENDING " + $d) }
}
$um = Join-Path $root 'docs\用户手册.md'
if (Test-Path $um) {
    Copy-Item $um -Destination (Join-Path $dist 'USER-GUIDE.zh.md') -Force
    Write-Host '    OK USER-GUIDE.zh.md'
}

Write-Host ''
Write-Host '[8] PRIVACY CHECK: data/ must not contain user data'
$bad = Get-ChildItem $distData -Recurse -File | Where-Object { $_.Name -match 'index\.db|\.db$|\.onnx$|\.sqlite' }
if ($bad) { Write-Host ('    FAIL: ' + (($bad | ForEach-Object { $_.Name }) -join ', ')) }
else { Write-Host '    OK no index.db / model files' }
Write-Host ('    data/ contains: ' + ((Get-ChildItem $distData -Recurse -File | ForEach-Object { $_.Name }) -join ', '))

Write-Host ''
Write-Host '[9] dist summary'
$all = Get-ChildItem $dist -Recurse -File
$total = [math]::Round(($all | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host ("    files " + $all.Count + " / total " + $total + " MB")
$all | Sort-Object Length -Descending | Select-Object -First 6 | ForEach-Object {
    $n = $_.FullName.Replace($dist + '\', '')
    Write-Host ("    {0,10:N1} MB  {1}" -f ($_.Length / 1MB), $n)
}
