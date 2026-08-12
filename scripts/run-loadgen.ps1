[CmdletBinding()]
param(
    [ValidateRange(1, 4000)]
    [int]$Concurrency = 30,

    [int]$Seed,

    [uri]$BaseUrl = 'http://localhost:5107',

    [string]$AccessToken = $env:TICKETING_API_ACCESS_TOKEN,

    [ValidateSet('Read', 'Mixed', 'Comparison')]
    [string]$Workload = 'Read',

    [string]$ApiDirectory,

    [string]$LogDirectory = 'logs\loadgen',

    [ValidateRange(0.1, 86400)]
    [double]$Duration,

    [ValidateRange(1, 600)]
    [double]$RequestTimeout = 120,

    [ValidateRange(0.5, 60)]
    [double]$ReportInterval = 0.5,

    [switch]$Saturate,

    [switch]$Prompt,

    [switch]$NoPrompt
)

$ErrorActionPreference = 'Stop'

if ($Prompt -and $NoPrompt) {
    throw 'Use either -Prompt or -NoPrompt, not both.'
}

function Read-Choice {
    param(
        [string]$Message,
        [string[]]$Choices,
        [int]$DefaultIndex = 0
    )

    while ($true) {
        for ($index = 0; $index -lt $Choices.Count; $index++) {
            Write-Host "  $($index + 1)) $($Choices[$index])"
        }
        $answer = Read-Host "$Message [$($DefaultIndex + 1)]"
        if ([string]::IsNullOrWhiteSpace($answer)) {
            return $DefaultIndex
        }
        $selected = 0
        if ([int]::TryParse($answer, [ref]$selected) -and $selected -ge 1 -and $selected -le $Choices.Count) {
            return $selected - 1
        }
        Write-Warning "Enter a number from 1 to $($Choices.Count)."
    }
}

function Read-Number {
    param(
        [string]$Message,
        [double]$Default,
        [double]$Minimum,
        [double]$Maximum
    )

    while ($true) {
        $answer = Read-Host "$Message [$Default]"
        if ([string]::IsNullOrWhiteSpace($answer)) {
            return $Default
        }
        $value = 0.0
        if ([double]::TryParse(
            $answer,
            [Globalization.NumberStyles]::Float,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$value) -and $value -ge $Minimum -and $value -le $Maximum) {
            return $value
        }
        Write-Warning "Enter a number from $Minimum to $Maximum."
    }
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'LoadGen\LoadGen.csproj'
$durationWasSet = $PSBoundParameters.ContainsKey('Duration')
$seedWasSet = $PSBoundParameters.ContainsKey('Seed')
$isNonInteractive = [Console]::IsInputRedirected -or
    ([Environment]::GetCommandLineArgs() -contains '-NonInteractive')
$explicitParameters = @($PSBoundParameters.Keys | Where-Object { $_ -notin @('Prompt', 'NoPrompt') })
$usePrompt = $Prompt -or (-not $NoPrompt -and -not $isNonInteractive -and $explicitParameters.Count -eq 0)

if ($usePrompt) {
    Write-Host ''
    Write-Host 'Ticketing LoadGen setup' -ForegroundColor Cyan
    Write-Host 'Press Enter to accept each default.'
    Write-Host ''

    while ([string]::IsNullOrWhiteSpace($ApiDirectory)) {
        $ApiDirectory = Read-Host 'Directory containing TicketingApi.csproj'
    }

    $workloadIndex = Read-Choice 'Workload' @('Read - five read-only API operations', 'Mixed - reads and writes')
    $Workload = if ($workloadIndex -eq 0) { 'Read' } else { 'Mixed' }
    $Concurrency = [int](Read-Number 'Concurrency' 30 1 4000)
    $saturationAnswer = Read-Host 'Adaptively increase concurrency until HTTP 429 throttling appears? [y/N]'
    $Saturate = $saturationAnswer -match '^(y|yes)$'

    while ($true) {
        $durationAnswer = Read-Host 'Duration in seconds, or Enter for unlimited'
        if ([string]::IsNullOrWhiteSpace($durationAnswer)) {
            $durationWasSet = $false
            break
        }
        $parsedDuration = 0.0
        if ([double]::TryParse(
            $durationAnswer,
            [Globalization.NumberStyles]::Float,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$parsedDuration) -and $parsedDuration -ge 0.1 -and $parsedDuration -le 86400) {
            $Duration = $parsedDuration
            $durationWasSet = $true
            break
        }
        Write-Warning 'Enter a duration from 0.1 to 86400 seconds, or press Enter for unlimited.'
    }

    $RequestTimeout = Read-Number 'Request timeout in seconds' 120 1 600
    while ($true) {
        $urlAnswer = Read-Host "API base URL [$BaseUrl]"
        if ([string]::IsNullOrWhiteSpace($urlAnswer)) {
            break
        }
        $parsedUrl = $null
        if ([uri]::TryCreate($urlAnswer, [UriKind]::Absolute, [ref]$parsedUrl) -and
            $parsedUrl.Scheme -in @('http', 'https')) {
            $BaseUrl = $parsedUrl
            break
        }
        Write-Warning 'Enter an absolute HTTP or HTTPS URL.'
    }
    $ReportInterval = Read-Number 'Dashboard refresh interval in seconds' 0.5 0.5 60

    $seedAnswer = Read-Host 'Random seed, or Enter for a random seed'
    if ([string]::IsNullOrWhiteSpace($seedAnswer)) {
        $seedWasSet = $false
    }
    else {
        $parsedSeed = 0
        if (-not [int]::TryParse($seedAnswer, [ref]$parsedSeed)) {
            throw 'Seed must be a whole number.'
        }
        $Seed = $parsedSeed
        $seedWasSet = $true
    }

    $durationSummary = if ($durationWasSet) { "$Duration seconds" } else { 'unlimited' }
    Write-Host ''
    Write-Host 'Selected settings' -ForegroundColor Cyan
    Write-Host "  Target:      $ApiDirectory"
    Write-Host "  Workload:    $Workload"
    Write-Host "  Concurrency: $Concurrency"
    Write-Host "  Saturation:  $(if ($Saturate) { 'adaptive' } else { 'off' })"
    Write-Host "  Duration:    $durationSummary"
    Write-Host "  Timeout:     $RequestTimeout seconds"
    Write-Host "  URL:         $BaseUrl"
    $confirmation = Read-Host 'Start LoadGen? [Y/n]'
    if ($confirmation -match '^(n|no)$') {
        Write-Host 'Cancelled.'
        return
    }
}

if ([string]::IsNullOrWhiteSpace($ApiDirectory)) {
    throw 'ApiDirectory is required. Run interactively to be prompted, or pass -ApiDirectory with the directory containing TicketingApi.csproj.'
}

$apiDirectoryPath = if ([System.IO.Path]::IsPathRooted($ApiDirectory)) {
    $ApiDirectory
}
else {
    Join-Path $repositoryRoot $ApiDirectory
}
$apiDirectoryPath = [System.IO.Path]::GetFullPath($apiDirectoryPath)
$apiProjectPath = Join-Path $apiDirectoryPath 'TicketingApi.csproj'
$logDirectoryPath = if ([System.IO.Path]::IsPathRooted($LogDirectory)) {
    $LogDirectory
}
else {
    Join-Path $repositoryRoot $LogDirectory
}
$logDirectoryPath = [System.IO.Path]::GetFullPath($logDirectoryPath)
$appSettingsPath = Join-Path $repositoryRoot 'appsettings.json'
$targetUrl = $BaseUrl.AbsoluteUri.TrimEnd('/')
$openApiUrl = "$targetUrl/openapi/v1.json"
$loadProfile = if ($Workload -eq 'Mixed') { 'mixed' } else { 'comparison' }
$workloadName = if ($Workload -eq 'Mixed') { 'Mixed' } else { 'Read' }

if (-not (Test-Path -LiteralPath $apiProjectPath -PathType Leaf)) {
    throw "The API project does not exist at '$apiProjectPath'. Set -ApiDirectory to the directory containing TicketingApi.csproj."
}

if (-not $BaseUrl.IsLoopback -and $BaseUrl.Scheme -ne 'https') {
    throw 'A non-loopback API URL must use HTTPS.'
}

if ([string]::IsNullOrWhiteSpace($AccessToken)) {
    if ($BaseUrl.IsLoopback) {
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
    $loadProfile
    '--report-interval'
    $ReportInterval
    '--request-timeout'
    $RequestTimeout
)

if ($seedWasSet) {
    $loadGenArguments += @('--seed', $Seed)
}

if ($durationWasSet) {
    $loadGenArguments += @('--duration', $Duration)
}

if ($Saturate) {
    $loadGenArguments += '--saturate'
}

$durationDescription = if ($durationWasSet) { "$Duration seconds" } else { 'until Ctrl+C' }
$saturationDescription = if ($Saturate) { 'adaptive saturation enabled' } else { 'saturation off' }
Write-Host "Starting $workloadName LoadGen against $targetUrl using $apiProjectPath with base concurrency $Concurrency for $durationDescription (request timeout: $RequestTimeout seconds; $saturationDescription)."
Write-Host "Summary log directory: $logDirectoryPath"
if (-not $durationWasSet) {
    Write-Host 'Press Ctrl+C to stop and print final request-unit totals.'
}

$previousToken = $env:TICKETING_API_ACCESS_TOKEN
$previousLogDirectory = $env:LOADGEN_LOG_DIRECTORY
try {
    if (-not [string]::IsNullOrWhiteSpace($AccessToken)) {
        $env:TICKETING_API_ACCESS_TOKEN = $AccessToken
    }
    $env:LOADGEN_LOG_DIRECTORY = $logDirectoryPath

    & dotnet @loadGenArguments
    exit $LASTEXITCODE
}
finally {
    $env:TICKETING_API_ACCESS_TOKEN = $previousToken
    $env:LOADGEN_LOG_DIRECTORY = $previousLogDirectory
}