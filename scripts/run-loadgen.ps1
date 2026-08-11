[CmdletBinding()]
param(
    [ValidateRange(1, 4000)]
    [int]$Concurrency = 10,

    [int]$Seed,

    [uri]$BaseUrl = 'http://localhost:5107'
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'LoadGen\LoadGen.csproj'
$targetUrl = $BaseUrl.AbsoluteUri.TrimEnd('/')
$openApiUrl = "$targetUrl/openapi/v1.json"

try {
    $response = Invoke-WebRequest -Uri $openApiUrl -Method Get -TimeoutSec 5
    if ($response.StatusCode -ne 200) {
        throw "OpenAPI returned HTTP $($response.StatusCode)."
    }
}
catch {
    throw "Ticketing API is not ready at $targetUrl. Start the API before LoadGen. $($_.Exception.Message)"
}

$loadGenArguments = @(
    'run'
    '--project'
    $projectPath
    '--'
    '--concurrency'
    $Concurrency
    '--base-url'
    $targetUrl
)

if ($PSBoundParameters.ContainsKey('Seed')) {
    $loadGenArguments += @('--seed', $Seed)
}

Write-Host "Starting unlimited LoadGen against $targetUrl with base concurrency $Concurrency."
Write-Host 'Press Ctrl+C to stop and print final request-unit totals.'

& dotnet @loadGenArguments
exit $LASTEXITCODE