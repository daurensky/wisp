param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Push-Location $PSScriptRoot
try {
    dotnet restore Wisp.sln
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    dotnet test tests/Wisp.Core.Tests/Wisp.Core.Tests.csproj -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
    dotnet build src/Wisp.Desktop/Wisp.Desktop.csproj -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed.' }
    $output = Join-Path 'artifacts' "Wisp-$Runtime"
    dotnet publish src/Wisp.Desktop/Wisp.Desktop.csproj -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=false -o $output
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    $zip = Join-Path 'artifacts' "Wisp-$Runtime.zip"
    Compress-Archive -Path "$output/*" -DestinationPath $zip -Force
    Write-Host "Ready: $zip"
} finally {
    Pop-Location
}
