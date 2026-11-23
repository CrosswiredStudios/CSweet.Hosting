#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Health','Provision','Retire')][string]$Action = 'Health',
    [uri]$BaseUri = 'http://127.0.0.1:8198',
    [Parameter(Mandatory)][string]$KeyFile,
    [string]$OwnerId,
    [string]$OrderFile = 'hosting-test-order.json',
    [ValidateRange(1,120)][int]$TimeoutMinutes = 20
)
$ErrorActionPreference = 'Stop'
if (!$BaseUri.IsAbsoluteUri -or ($BaseUri.Scheme -ne 'https' -and !($BaseUri.Scheme -eq 'http' -and $BaseUri.IsLoopback))) {
    throw 'Use HTTPS or a loopback address reached directly or through an SSH forward.'
}
$base = $BaseUri.GetLeftPart([UriPartial]::Authority)
$key = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $KeyFile)).Trim()
if (!$key) { throw 'Empty Hosting service key.' }
$headers = @{Authorization="Bearer $key"}
function CallApi($method, $path, $body) {
    $options = @{ Uri="$base$path"; Method=$method; Headers=$headers; SkipHttpErrorCheck=$true; MaximumRedirection=0; TimeoutSec=15 }
    if ($null -ne $body) { $options.ContentType='application/json'; $options.Body=$body }
    Invoke-WebRequest @options
}

if ($Action -eq 'Health') {
    $health = CallApi GET '/health' $null
    if ($health.StatusCode -ne 200 -or ($health.Content | ConvertFrom-Json).status -ne 'up') { throw 'Health check failed.' }
    $auth = CallApi PUT '/v1/deployments/00000000-0000-0000-0000-000000000001' '{}'
    if ($auth.StatusCode -ne 400) { throw "Authenticated API check returned HTTP $($auth.StatusCode)." }
    $version = ($health.Content | ConvertFrom-Json).version
    Write-Output "Hosting $version is healthy. The API accepted the operator credential at $base."
    return
}

if (Test-Path -LiteralPath $OrderFile) {
    $saved = Get-Content -LiteralPath $OrderFile -Raw | ConvertFrom-Json -AsHashtable
    if ($saved.base -ne $base) { throw 'This order belongs to another API address.' }
    $body = [string]$saved.body
    $order = $body | ConvertFrom-Json
    if ($OwnerId -and $order.ownerId -cne $OwnerId) { throw 'This saved order belongs to another owner.' }
} else {
    if ($Action -eq 'Retire' -or [string]::IsNullOrWhiteSpace($OwnerId)) { throw 'Supply an existing test order file or specify an owner for a new order.' }
    $order = [ordered]@{ orderId=[guid]::NewGuid().ToString(); ownerId=$OwnerId; paidAt=[DateTimeOffset]::UtcNow.ToString('o'); linkedOffices=0; standardOffices=0; teamOffices=0 }
    $body = $order | ConvertTo-Json -Compress
    $saved = @{base=$base;body=$body} | ConvertTo-Json
    $file = [IO.File]::Open($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OrderFile), 'CreateNew', 'Write', 'None')
    try { $bytes=[Text.Encoding]::UTF8.GetBytes($saved); $file.Write($bytes,0,$bytes.Length) } finally { $file.Dispose() }
}
$id = [guid]::Parse([string]$order.orderId)
$path = "/v1/deployments/$id"
$deadline = [DateTimeOffset]::UtcNow.AddMinutes($TimeoutMinutes)
Write-Output "$Action for test order $id (saved in $OrderFile)"
do {
    if ($Action -eq 'Provision') {
        $result = CallApi PUT $path $body
        if ($result.StatusCode -ne 200) { throw "Order $id returned HTTP $($result.StatusCode)." }
        $receipt = $result.Content | ConvertFrom-Json
        if ([guid]$receipt.orderId -ne $id) { throw 'Mismatched order.' }
        if ($receipt.state -ceq 'Ready') {
            $expected = 'https://hq-' + $id.ToString('N') + '.c-sweet.app/'
            if ($receipt.workspaceUrl -cne $expected) { throw 'Unexpected workspace URL.' }
            Write-Output "Ready $expected"
            return
        }
        if ($receipt.state -cne 'Setting up' -or $receipt.workspaceUrl) { throw 'Unexpected deployment state.' }
    } else {
        $result = CallApi DELETE $path $null
        if ($result.StatusCode -eq 204) { Write-Output "Retired $id"; return }
        if ($result.StatusCode -notin 202,409) { throw "Retirement returned HTTP $($result.StatusCode)." }
    }
    Start-Sleep -Seconds 5
} while ([DateTimeOffset]::UtcNow -lt $deadline)
throw "Timed out. Reuse $OrderFile to resume the same test."
