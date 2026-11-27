param(
    [ValidateRange(1024, 65535)]
    [int]$Port = 55432
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$state = Join-Path $repo '_local_sources/lab'
New-Item -ItemType Directory -Path $state -Force | Out-Null
$passwordFile = Join-Path $state 'postgres-password'
if (-not [IO.File]::Exists($passwordFile)) {
    $password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
    [IO.File]::WriteAllText($passwordFile, $password)
}
$env:CAPSTONE_PASSWORD = [IO.File]::ReadAllText($passwordFile)
$env:CAPSTONE_PORT = [string]$Port
$env:CAPSTONE_DB = "Host=127.0.0.1;Port=$Port;Database=capstone;Username=capstone;Password=$($env:CAPSTONE_PASSWORD);SSL Mode=Disable;Maximum Pool Size=64"

docker compose -f (Join-Path $PSScriptRoot 'compose.yaml') up -d --wait
if ($LASTEXITCODE -ne 0) { throw 'Capstone PostgreSQL did not become healthy' }
Write-Output "Capstone PostgreSQL is ready on 127.0.0.1:$Port."
