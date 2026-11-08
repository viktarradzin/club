[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://localhost:5080',
    [string]$Email = 'user001@club.test',
    [string]$Password = 'Password123!',
    [long]$ClassId = 109,
    [ValidateRange(1, 2147483647)]
    [int]$Seats = 2,
    [ValidateNotNullOrEmpty()]
    [string]$IdempotencyKey = 'manual-success-01',
    [ValidateRange(0, 3600)]
    [int]$InitialWaitSeconds = 61
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

function Send-JsonPost {
    param(
        [string]$Path,
        [string]$Body,
        [string]$Token,
        [string]$Key
    )

    $request = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::Post, ($BaseUrl.TrimEnd('/') + $Path))
    $response = $null
    try {
        $request.Content = [System.Net.Http.StringContent]::new(
            $Body, [System.Text.Encoding]::UTF8, 'application/json')
        if ($Token) {
            $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new(
                'Bearer', $Token)
        }
        if ($Key) {
            [void]$request.Headers.TryAddWithoutValidation('Idempotency-Key', $Key)
        }

        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        $responseBody = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $retryAfter = $null
        if ($response.Headers.Contains('Retry-After')) {
            $retryAfter = ($response.Headers.GetValues('Retry-After') -join ',')
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

function Assert-SameBooking {
    param($Actual, $Expected)

    foreach ($field in @('id', 'classId', 'seats', 'status', 'createdAt')) {
        if ($null -eq $Actual.$field -or
            [string]$Actual.$field -cne [string]$Expected.$field) {
            throw "The replay changed response field '$field'."
        }
    }
}

$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds(15)

try {
    Write-Host "Do not use other assignment endpoints as $Email during this check."
    Write-Host "Waiting $InitialWaitSeconds seconds for previous requests to leave the rate-limit window..."
    Start-Sleep -Seconds $InitialWaitSeconds

    $loginBody = @{ email = $Email; password = $Password } | ConvertTo-Json -Compress
    $login = Send-JsonPost -Path '/auth/login' -Body $loginBody
    if ($login.Status -ne 200) {
        throw "Login failed: HTTP $($login.Status). $($login.Body)"
    }
    $token = ($login.Body | ConvertFrom-Json).accessToken
    if ([string]::IsNullOrWhiteSpace($token)) { throw 'Login response has no accessToken.' }

    $body = @{ classId = $ClassId; seats = $Seats } | ConvertTo-Json -Compress
    Write-Host "POST /enrollments: classId=$ClassId, seats=$Seats, key=$IdempotencyKey"
    Write-Host 'An unused key creates one booking and spends visits once; an existing matching key replays it.'

    $firstBooking = $null
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    for ($number = 1; $number -le 61; $number++) {
        $result = Send-JsonPost -Path '/enrollments' -Body $body -Token $token -Key $IdempotencyKey
        Write-Host ('{0,2}/61 -> HTTP {1}' -f $number, $result.Status)

        if ($watch.Elapsed.TotalSeconds -ge 60) {
            throw 'The burst took 60 seconds or more. Repeat the check when the API responds faster.'
        }

        if ($number -le 60) {
            if ($result.Status -ne 201) {
                throw "Expected 201 on request $number, got $($result.Status). $($result.Body) Check the booking parameters, key, and other requests from this user."
            }
            $booking = $result.Body | ConvertFrom-Json
            if ($number -eq 1) {
                if ($null -eq $booking.id -or $null -eq $booking.createdAt -or
                    $booking.classId -ne $ClassId -or $booking.seats -ne $Seats -or
                    $booking.status -ne 'active') {
                    throw "Unexpected booking response: $($result.Body)"
                }
                $firstBooking = $booking
            }
            Assert-SameBooking -Actual $booking -Expected $firstBooking
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
    $problem = $result.Body | ConvertFrom-Json
    if ($problem.status -ne 429) { throw "Unexpected error body: $($result.Body)" }

    Write-Host "PASS: 60 responses with 201 and the same booking id=$($firstBooking.id); request 61 returned 429."
    Write-Host "Retry-After: $retrySeconds seconds. Response: $($result.Body)"
    Write-Host "Waiting $($retrySeconds + 1) seconds to check recovery..."
    Start-Sleep -Seconds ($retrySeconds + 1)

    $recovery = Send-JsonPost -Path '/enrollments' -Body $body -Token $token -Key $IdempotencyKey
    if ($recovery.Status -ne 201) {
        throw "Expected 201 after waiting, got $($recovery.Status). $($recovery.Body)"
    }
    Assert-SameBooking -Actual ($recovery.Body | ConvertFrom-Json) -Expected $firstBooking
    Write-Host "PASS: requests are accepted again; booking id=$($firstBooking.id) is unchanged."
}
finally {
    $client.Dispose()
}
