# dem2mesh.cs のテストを、Windows に入っている .NET Framework の csc でビルドして実行する
# 使い方（リポジトリのフォルダで）:
#   powershell -ExecutionPolicy Bypass -File tests\run_tests.ps1
#   powershell -ExecutionPolicy Bypass -File tests\run_tests.ps1 -SampleDir <GMLのフォルダ>
# SampleDir を省くと sample\FG-GML-513253-DEM5A-* を探す。見つからなければ実データのテストは SKIP になる
# 終了コード: 0 = すべて成功、1 = テストの失敗あり、2 = ビルドできない
# このファイルは日本語を含むので、UTF-8（BOM付き）で保存すること（Windows PowerShell 5.1 が文字化けしないように）
param([string]$SampleDir = "")

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$build = Join-Path $here 'build'
New-Item -ItemType Directory -Force $build | Out-Null

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { Write-Host "csc.exe が見つかりません: $csc"; exit 2 }

# .NET Framework には Math.Atanh がないので、テスト用のコピーだけ MathX.Atanh（stubs.cs）に置き換える
$src = [IO.File]::ReadAllText((Join-Path $root 'dem2mesh.cs'), [Text.Encoding]::UTF8)
[IO.File]::WriteAllText((Join-Path $build 'script.cs'), $src.Replace('Math.Atanh', 'MathX.Atanh'), (New-Object Text.UTF8Encoding($false)))

$exe = Join-Path $build 'tests.exe'
& $csc -nologo -codepage:65001 -nowarn:0649,0168 "-out:$exe" `
  -r:System.Xml.dll -r:System.Core.dll -r:System.Drawing.dll `
  (Join-Path $here 'stubs.cs') (Join-Path $here 'gml.cs') (Join-Path $here 'tests.cs') (Join-Path $build 'script.cs')
if ($LASTEXITCODE -ne 0) { Write-Host "ビルドに失敗しました（終了コード $LASTEXITCODE）"; exit 2 }

if ($SampleDir -eq "") {
  $found = Get-ChildItem -Path (Join-Path $root 'sample') -Directory -Filter 'FG-GML-513253-DEM5A-*' -ErrorAction SilentlyContinue | Select-Object -First 1
  if ($found) { $SampleDir = $found.FullName }
}

# 実行結果は加工せずにそのまま表示し、終了コードをそのまま返す
if ($SampleDir -eq "") { & $exe } else { & $exe $SampleDir }
exit $LASTEXITCODE
