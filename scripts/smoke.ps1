param([string]$BaseUrl = 'http://localhost:8080')
$ErrorActionPreference = 'Stop'

function Assert-Equal($Expected, $Actual, [string]$Description) {
    if ($Expected -ne $Actual) { throw "$Description`: expected $Expected; got $Actual" }
}

$live = Invoke-RestMethod "$BaseUrl/health/live"
$ready = Invoke-RestMethod "$BaseUrl/health/ready"
Assert-Equal 'Healthy' $live.status 'Liveness'
Assert-Equal 'Healthy' $ready.status 'Database readiness'
$swagger = Invoke-RestMethod "$BaseUrl/swagger/v1/swagger.json"
Assert-Equal 'NovaWallet Ledger API' $swagger.info.title 'Swagger title'
$token = Invoke-RestMethod -Method Post "$BaseUrl/dev/token"
$headers = @{ Authorization = "Bearer $($token.accessToken)" }
$suffix = [Guid]::NewGuid().ToString('N')
$headers['X-Correlation-ID'] = "smoke-$suffix"
$source = Invoke-RestMethod -Method Post "$BaseUrl/api/wallets" -Headers $headers -ContentType 'application/json' -Body (@{ customerId = "smoke-source-$suffix" } | ConvertTo-Json)
$destination = Invoke-RestMethod -Method Post "$BaseUrl/api/wallets" -Headers $headers -ContentType 'application/json' -Body (@{ customerId = "smoke-destination-$suffix" } | ConvertTo-Json)
Assert-Equal 0 $source.balanceKobo 'Starting balance'
Assert-Equal 'NGN' $source.currency 'Currency'
$null = Invoke-RestMethod -Method Post "$BaseUrl/api/wallets/$($source.id)/credits" -Headers $headers -ContentType 'application/json' -Body '{"amountKobo":10000000}'
$headers['Idempotency-Key'] = "smoke-$suffix"
$payload = @{ sourceWalletId = $source.id; destinationWalletId = $destination.id; amountKobo = 1000000 } | ConvertTo-Json
$transfer = Invoke-RestMethod -Method Post "$BaseUrl/api/transfers" -Headers $headers -ContentType 'application/json' -Body $payload
$replay = Invoke-RestMethod -Method Post "$BaseUrl/api/transfers" -Headers $headers -ContentType 'application/json' -Body $payload
Assert-Equal $transfer.reference $replay.reference 'Idempotent replay reference'
$balanceResponse = Invoke-WebRequest -UseBasicParsing "$BaseUrl/api/wallets/$($source.id)/balance" -Headers $headers
Assert-Equal "smoke-$suffix" $balanceResponse.Headers['X-Correlation-ID'] 'Correlation response header'
$balance = $balanceResponse.Content | ConvertFrom-Json
Assert-Equal 9000000 $balance.balanceKobo 'Source balance after replay'
$balance = Invoke-RestMethod "$BaseUrl/api/wallets/$($destination.id)/balance" -Headers $headers
Assert-Equal 1000000 $balance.balanceKobo 'Destination balance after replay'
$statement = Invoke-RestMethod "$BaseUrl/api/wallets/$($source.id)/statement?page=1&pageSize=20" -Headers $headers
Assert-Equal 2 $statement.totalCount 'Source statement count'
Write-Output "Smoke check passed: health, readiness, correlation, Swagger, JWT, create, credit, transfer, replay, balances and statement. Reference: $($transfer.reference)"
