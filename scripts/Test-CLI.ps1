param([switch]$Sql, [ValidateSet('Debug','Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$names = 'SFM_RUN_SQL_TESTS','SFM_ENVIRONMENT','SFM_EXISTING_TEST_DATABASE','SFM_STORAGE_TEST_ROOT','SFM_CONNECTION_STRING','SFM_STORAGE_ROOT','SFM_CONFIG_DIRECTORY'
$saved = @{}
foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    $testArea = Join-Path ([IO.Path]::GetTempPath()) ('sfm-cli-run-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $testArea | Out-Null
    $env:SFM_RUN_SQL_TESTS = if ($Sql) { '1' } else { $null }
    $env:SFM_ENVIRONMENT = 'Development'
    $env:SFM_EXISTING_TEST_DATABASE = $null
    $env:SFM_STORAGE_TEST_ROOT = $testArea
    $env:SFM_STORAGE_ROOT = Join-Path $testArea 'ConfigurationOnlyStorage'
    $env:SFM_CONFIG_DIRECTORY = $testArea
    # Configuration-only fallback: actual SQL fixtures always generate their own GUID catalog.
    $env:SFM_CONNECTION_STRING = 'Server=.\SQLEXPRESS;Database=SecureFileManagerTests_configuration_only;Integrated Security=True;Encrypt=True;TrustServerCertificate=True'
    $results = Join-Path $repository ('artifacts\tests\' + [guid]::NewGuid().ToString('N'))
    & dotnet test (Join-Path $repository 'SecureFileManager-CLI.sln') -c $Configuration --logger 'trx;LogFileName=tests.trx' --results-directory $results
    if ($LASTEXITCODE -ne 0) { throw "Тесты завершились с кодом $LASTEXITCODE. Результаты: $results" }
    Write-Host "Результаты: $results"
    Write-Host "Изолированные файловые данные оставлены для разбора: $testArea"
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
}
