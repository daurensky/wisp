param(
    [Parameter(Mandatory)][string]$BaselineExecutable,
    [Parameter(Mandatory)][string]$CurrentExecutable,
    [Parameter(Mandatory)][string]$BaselineRevision,
    [Parameter(Mandatory)][string]$CurrentRevision,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(2, 10)][int]$Runs = 3
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path
$fixtureDirectory = Join-Path ([IO.Path]::GetTempPath()) ('wisp-ui-benchmark-' + [guid]::NewGuid().ToString('N'))
$variants = @{
    baseline = @{ executable = $BaselineExecutable; revision = $BaselineRevision }
    current = @{ executable = $CurrentExecutable; revision = $CurrentRevision }
}

function Invoke-Probe([string]$Executable, [string[]]$Arguments) {
    # File arguments generated here contain no quotes; quote paths with spaces for Start-Process.
    $quoted = $Arguments | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }
    $process = Start-Process -FilePath $Executable -ArgumentList $quoted -PassThru -NoNewWindow
    if (!$process.WaitForExit(60000)) {
        $process.Kill($true)
        throw "Benchmark exceeded 60 seconds: $Executable"
    }
    if ($process.ExitCode -ne 0) { throw "Benchmark failed ($($process.ExitCode)): $Executable; inspect raw JSON." }
}

try {
    foreach ($label in @('baseline', 'current')) {
        $fixture = Join-Path $fixtureDirectory $label
        Invoke-Probe $variants[$label].executable @('--mode', 'prepare', '--fixture', $fixture)
    }
    for ($run = 0; $run -lt $Runs; $run++) {
        # Alternate order to reduce a fixed baseline-first scheduling/cache bias.
        $order = if ($run % 2 -eq 0) { @('baseline', 'current') } else { @('current', 'baseline') }
        foreach ($label in $order) {
            $variant = $variants[$label]
            Invoke-Probe $variant.executable @(
                '--fixture', (Join-Path $fixtureDirectory $label),
                '--output', (Join-Path $OutputDirectory "$label-$run.json"),
                '--label', $label, '--revision', $variant.revision, '--run', "$run"
            )
        }
    }
}
finally {
    if (Test-Path $fixtureDirectory) { Remove-Item -Recurse -Force $fixtureDirectory }
}

python (Join-Path $PSScriptRoot 'summarize.py') $OutputDirectory
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
