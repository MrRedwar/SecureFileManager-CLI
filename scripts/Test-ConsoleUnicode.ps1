$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repository 'tests\ConsoleUnicodeProbe\ConsoleUnicodeProbe.csproj'
& dotnet build $project -c Debug
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать проверку Unicode-консоли.' }
$folder = Join-Path $repository 'tests\ConsoleUnicodeProbe\bin\Debug\net9.0-windows'
$process = Start-Process -FilePath (Join-Path $folder 'ConsoleUnicodeProbe.exe') -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(15000)) { $process.Kill(); throw 'Истекло время проверки Unicode-консоли.' }
Get-Content -LiteralPath (Join-Path $folder 'result.txt')
if ($process.ExitCode -ne 0) { throw 'Проверка Unicode-консоли не пройдена.' }
