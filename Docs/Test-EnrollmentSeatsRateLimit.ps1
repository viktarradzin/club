[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateRange([long]1, [long]::MaxValue)]
    [long]$EnrollmentId,
    [string]$BaseUrl = 'http://localhost:5080',
    [string]$Email = 'user001@club.test',
    [string]$Password = 'Password123!'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

function Send-JsonRequest {
    param([string]$Method, [string]$Path, [string]$Body, [string]$Token)

    $request = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::new($Method), ($BaseUrl.TrimEnd('/') + $Path))
    $response = $null
    try {
        if ($Body) {
            $request.Content = [System.Net.Http.StringContent]::new(
                $Body, [System.Text.Encoding]::UTF8, 'application/json')
        }
        if ($Token) {
            $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new(
                'Bearer', $Token)
        }
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        $responseBody = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $retryAfter = $null
        if ($response.Headers.Contains('Retry-After')) {
            $retryAfter = $response.Headers.GetValues('Retry-After') -join ','
        }
        return [pscustomobject]@{
            Status = [int]$response.StatusCode
            Body = $responseBody
            RetryAfter = $retryAfter
        }
    }
    finally {
        if ($null -ne $response) { $response.Dispose() }
        $request.Dispose()
    }
}

function Assert-UnchangedCard {
    param($Result, [string]$Expected)

    if ($Result.Status -ne 200) {
        throw "Expected 200, got $($Result.Status). $($Result.Body)"
    }
    $actual = $Result.Body | ConvertFrom-Json | ConvertTo-Json -Depth 10 -Compress
    if ($actual -cne $Expected) {
        throw "The enrollment card changed during the check. $($Result.Body)"
    }
}

$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds(15)

try {
    $loginBody = @{ email = $Email; password = $Password } | ConvertTo-Json -Compress
    $login = Send-JsonRequest -Method POST -Path '/auth/login' -Body $loginBody
    if ($login.Status -ne 200) { throw "Login failed: HTTP $($login.Status). $($login.Body)" }
    $token = ($login.Body | ConvertFrom-Json).accessToken
    if ([string]::IsNullOrWhiteSpace($token)) { throw 'Login response has no accessToken.' }

    $initial = Send-JsonRequest -Method GET -Path "/enrollments/$EnrollmentId" -Token $token
    if ($initial.Status -ne 200) {
        throw "Cannot read enrollment: HTTP $($initial.Status). $($initial.Body)"
    }
    $card = $initial.Body | ConvertFrom-Json
    if ($card.id -ne $EnrollmentId -or $card.status -ne 'active' -or
        $null -eq $card.seats -or $card.seats -lt 1 -or
        $null -eq $card.class.room -or $null -eq $card.pass -or
        $card.class.isCancelled -ne $false) {
        throw "Expected an active enrollment with a full card. $($initial.Body)"
    }
    if ([DateTimeOffset]::Parse($card.class.startsAt) -le [DateTimeOffset]::UtcNow.AddMinutes(5)) {
        throw 'Choose a class that starts more than five minutes from now.'
    }
    $expected = $initial.Body | ConvertFrom-Json | ConvertTo-Json -Depth 10 -Compress
    $body = @{ seats = $card.seats } | ConvertTo-Json -Compress
    $path = "/enrollments/$EnrollmentId/seats"

    Write-Host "Testing POST $path as $Email with the current seats=$($card.seats)."
    Write-Host 'Do not use other assignment endpoints as this user or change this booking during the check.'
    Write-Host 'Waiting 61 seconds, including for the initial GET to leave the rate-limit window...'
    Start-Sleep -Seconds 61

    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    for ($number = 1; $number -le 61; $number++) {
        $result = Send-JsonRequest -Method POST -Path $path -Body $body -Token $token
        Write-Host ('{0,2}/61 -> HTTP {1}' -f $number, $result.Status)
        if ($watch.Elapsed.TotalSeconds -ge 60) {
            throw 'The burst took 60 seconds or more. Repeat when the API responds faster.'
        }
        if ($number -le 60) {
            Assert-UnchangedCard -Result $result -Expected $expected
        }
        elseif ($result.Status -ne 429) {
            throw "Expected 429 on request 61, got $($result.Status). $($result.Body)"
        }
    }
    $watch.Stop()

    $retrySeconds = 0
    if (-not [int]::TryParse([string]$result.RetryAfter, [ref]$retrySeconds) -or
        $retrySeconds -lt 1 -or $retrySeconds -gt 60) {
        throw "Expected Retry-After between 1 and 60 seconds, got '$($result.RetryAfter)'."
    }
    if (($result.Body | ConvertFrom-Json).status -ne 429) {
        throw "Unexpected error body: $($result.Body)"
    }
    Write-Host "PASS: 60 unchanged cards with 200; request 61 returned 429, Retry-After=$retrySeconds."
    Write-Host "Waiting $($retrySeconds + 1) seconds to check recovery..."
    Start-Sleep -Seconds ($retrySeconds + 1)

    $recovery = Send-JsonRequest -Method POST -Path $path -Body $body -Token $token
    Assert-UnchangedCard -Result $recovery -Expected $expected
    $final = Send-JsonRequest -Method GET -Path "/enrollments/$EnrollmentId" -Token $token
    Assert-UnchangedCard -Result $final -Expected $expected
    Write-Host 'PASS: requests are accepted again; seats and pass balance are unchanged.'
}
finally {
    $client.Dispose()
}
