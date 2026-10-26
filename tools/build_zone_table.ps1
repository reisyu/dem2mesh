# 系の自動判定に使う「3次メッシュ → 系」の表を作り直して、dem2mesh.cs に埋め込む
# 使い方（リポジトリのフォルダで）:
#   powershell -ExecutionPolicy Bypass -File tools\build_zone_table.ps1 -Download
#   powershell -ExecutionPolicy Bypass -File tools\build_zone_table.ps1 -CsvDir <CSVのフォルダ>
# -Download を付けると、総務省統計局「市区町村別メッシュ・コード一覧」の47都道府県のCSVを tools\stat_mesh に取得する（約11 MB）
# 表を作り直したら、テスト（tests\run_tests.ps1）を実行し、dem2mesh.gh の中のコードも貼り直すこと
# このファイルは日本語を含むので、UTF-8（BOM付き）で保存すること
param([string]$CsvDir = "", [switch]$Download)

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
if ($CsvDir -eq "") { $CsvDir = Join-Path $here 'stat_mesh' }
$build = Join-Path $here 'build'
New-Item -ItemType Directory -Force $build | Out-Null

if ($Download) {
  New-Item -ItemType Directory -Force $CsvDir | Out-Null
  foreach ($i in 1..47) {
    $p = '{0:D2}' -f $i
    $dest = Join-Path $CsvDir "$p.csv"
    Invoke-WebRequest -UseBasicParsing -Uri "https://www.stat.go.jp/data/mesh/csv/$p.csv" -OutFile $dest
  }
  Write-Host "CSVを取得しました: $CsvDir"
}

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { Write-Host "csc.exe が見つかりません: $csc"; exit 2 }
$exe = Join-Path $build 'build_zone_table.exe'
& $csc -nologo -codepage:65001 "-out:$exe" -r:System.Core.dll (Join-Path $here 'build_zone_table.cs')
if ($LASTEXITCODE -ne 0) { Write-Host "ビルドに失敗しました（終了コード $LASTEXITCODE）"; exit 2 }

& $exe $CsvDir (Join-Path $root 'dem2mesh.cs') $build
exit $LASTEXITCODE
