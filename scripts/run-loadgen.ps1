[CmdletBinding()]
param(
    [ValidateRange(1, 4000)]
    [int]$Concurrency = 50,

    [int]$Seed,

    [uri]$BaseUrl = 'http://localhost:5107',

    [string]$AccessToken = $env:TICKETING_API_ACCESS_TOKEN,

    [ValidateSet('Comparison', 'Mixed')]
    [string]$Workload = 'Comparison',

    [string]$ApiDirectory = 'TicketingApi',

    [string]$RunLabel,

    [ValidateRange(0.1, 86400)]
    [double]$Duration,

    [ValidateRange(1, 600)]
    [double]$RequestTimeout = 120,

    [ValidateRange(0.5, 60)]
    [double]$ReportInterval = 2
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'LoadGen\LoadGen.csproj'
$apiDirectoryPath = if ([System.IO.Path]::IsPathRooted($ApiDirectory)) {
    $ApiDirectory
}
else {
    Join-Path $repositoryRoot $ApiDirectory
}
$apiDirectoryPath = [System.IO.Path]::GetFullPath($apiDirectoryPath)
$apiProjectPath = Join-Path $apiDirectoryPath 'TicketingApi.csproj'
$appSettingsPath = Join-Path $repositoryRoot 'appsettings.json'
$targetUrl = $BaseUrl.AbsoluteUri.TrimEnd('/')
$openApiUrl = "$targetUrl/openapi/v1.json"

if ([string]::IsNullOrWhiteSpace($RunLabel)) {
    $rootApiDirectory = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'TicketingApi'))
    if ($apiDirectoryPath -eq $rootApiDirectory) {
        $RunLabel = 'root'
    }
    elseif ((Split-Path -Leaf $apiDirectoryPath) -eq 'TicketingApi') {
        $RunLabel = Split-Path -Leaf (Split-Path -Parent $apiDirectoryPath)
    }
    else {
        $RunLabel = Split-Path -Leaf $apiDirectoryPath
    }
}

if (-not $BaseUrl.IsLoopback -and $BaseUrl.Scheme -ne 'https') {
    throw 'A non-loopback API URL must use HTTPS.'
}

if ([string]::IsNullOrWhiteSpace($AccessToken)) {
    if ($BaseUrl.IsLoopback) {
        if (-not (Test-Path -LiteralPath $apiProjectPath -PathType Leaf)) {
            throw "The API project does not exist at '$apiProjectPath'. Set -ApiDirectory to the directory containing TicketingApi.csproj."
        }

        $scopes = @('Ticketing.Read')
        if ($Workload -eq 'Mixed') {
            $scopes += 'Ticketing.Write'
        }

        Write-Host "No access token supplied; creating a local development token with scopes: $($scopes -join ', ')."
        $jwtArguments = @(
            'user-jwts'
            'create'
            '--project'
            $apiProjectPath
            '--appsettings-file'
            $appSettingsPath
            '--audience'
            $targetUrl
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
    $Workload.ToLowerInvariant()
    '--report-interval'
    $ReportInterval
    '--request-timeout'
    $RequestTimeout
    '--run-label'
    $RunLabel
)

if ($PSBoundParameters.ContainsKey('Seed')) {
    $loadGenArguments += @('--seed', $Seed)
}

if ($PSBoundParameters.ContainsKey('Duration')) {
    $loadGenArguments += @('--duration', $Duration)
}

$durationDescription = if ($PSBoundParameters.ContainsKey('Duration')) { "$Duration seconds" } else { 'until Ctrl+C' }
Write-Host "Starting run '$RunLabel': $Workload LoadGen against $targetUrl with base concurrency $Concurrency for $durationDescription (request timeout: $RequestTimeout seconds)."
if (-not $PSBoundParameters.ContainsKey('Duration')) {
    Write-Host 'Press Ctrl+C to stop and print final request-unit totals.'
}

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