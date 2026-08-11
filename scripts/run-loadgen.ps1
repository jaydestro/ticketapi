[CmdletBinding()]
param(
    [ValidateRange(1, 4000)]
    [int]$Concurrency = 10,

    [int]$Seed,

    [uri]$BaseUrl = 'http://localhost:5107',

    [string]$AccessToken = $env:TICKETING_API_ACCESS_TOKEN,

    [ValidateSet('Comparison', 'Mixed')]
    [string]$Profile = 'Comparison',

    [ValidateRange(0.5, 60)]
    [double]$ReportInterval = 2
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'LoadGen\LoadGen.csproj'
$apiProjectPath = Join-Path $repositoryRoot 'TicketingApi\TicketingApi.csproj'
$targetUrl = $BaseUrl.AbsoluteUri.TrimEnd('/')
$openApiUrl = "$targetUrl/openapi/v1.json"

if (-not $BaseUrl.IsLoopback -and $BaseUrl.Scheme -ne 'https') {
    throw 'A non-loopback API URL must use HTTPS.'
}

if ([string]::IsNullOrWhiteSpace($AccessToken)) {
    if ($BaseUrl.IsLoopback) {
        $scopes = @('Ticketing.Read')
        if ($Profile -eq 'Mixed') {
            $scopes += 'Ticketing.Write'
        }

        Write-Host "No access token supplied; creating a local development token with scopes: $($scopes -join ', ')."
        $jwtArguments = @(
            'user-jwts'
            'create'
            '--project'
            $apiProjectPath
            '--appsettings-file'
            '..\appsettings.json'
        )
        foreach ($scope in $scopes) {
            $jwtArguments += @('--scope', $scope)
        }
        $jwtArguments += @('--output', 'token')
        $AccessToken = & dotnet @jwtArguments

        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($AccessToken)) {
            throw 'Could not create a local development token. Run the token command in Docs/loadgen.md and restart the API.'
        }

        $AccessToken = $AccessToken.Trim()
    }
    else {
        throw 'A deployed API requires -AccessToken or TICKETING_API_ACCESS_TOKEN.'
    }
}

try {
    $headers = @{}
    if (-not [string]::IsNullOrWhiteSpace($AccessToken)) {
        $headers.Authorization = "Bearer $AccessToken"
    }

    $response = Invoke-WebRequest -Uri $openApiUrl -Method Get -Headers $headers -TimeoutSec 5
    if ($response.StatusCode -ne 200) {
        throw "OpenAPI returned HTTP $($response.StatusCode)."
    }
}
catch {
    if ($_.Exception.Response.StatusCode -eq 401) {
        throw 'The Ticketing API rejected the access token. For local development, rebuild and restart the API so it loads the current user-jwts signing configuration.'
    }

    if ($_.Exception.Response.StatusCode -eq 403) {
        throw 'The access token lacks Ticketing.Read permission.'
    }

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
    '--profile'
    $Profile.ToLowerInvariant()
    '--report-interval'
    $ReportInterval
)

if ($PSBoundParameters.ContainsKey('Seed')) {
    $loadGenArguments += @('--seed', $Seed)
}

Write-Host "Starting unlimited $Profile LoadGen against $targetUrl with base concurrency $Concurrency."
Write-Host 'Press Ctrl+C to stop and print final request-unit totals.'

$previousToken = $env:TICKETING_API_ACCESS_TOKEN
try {
    if (-not [string]::IsNullOrWhiteSpace($AccessToken)) {
        $env:TICKETING_API_ACCESS_TOKEN = $AccessToken
    }

    & dotnet @loadGenArguments
    exit $LASTEXITCODE
}
finally {
    $env:TICKETING_API_ACCESS_TOKEN = $previousToken
}