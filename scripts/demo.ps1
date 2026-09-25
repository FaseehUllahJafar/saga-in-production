# Places one order and prints each step of the saga's journal as it changes, so you
# can watch it go forward and, when it fails, back. Run while `aspire run` is up.
#   pwsh scripts/demo.ps1                          # fails at capture, compensates backwards
#   pwsh scripts/demo.ps1 -CardToken tok_visa      # completes
#   pwsh scripts/demo.ps1 -CardToken tok_capture_down   # parks for a human (about 8 minutes)
param(
    [string]$CardToken = "tok_expired_auth",
    [string]$Orders = "http://localhost:5092"
)

$body = @{
    customerEmail   = "demo@example.com"
    cardToken       = $CardToken
    shippingAddress = "1 Main St"
    lines           = @(@{ sku = "BOOK-DDD"; quantity = 1; unitPrice = 30 })
} | ConvertTo-Json -Depth 4

$key = [guid]::NewGuid().ToString()
$order = Invoke-RestMethod -Method Post -Uri "$Orders/orders" -Body $body -ContentType "application/json" -Headers @{ "Idempotency-Key" = $key }
$id = $order.orderId
Write-Host ""
Write-Host "POST /orders  card=$CardToken" -ForegroundColor Cyan
Write-Host "saga.id = $id" -ForegroundColor Cyan
Write-Host ""

$seen = @{}
$final = "Completed", "Cancelled", "CompensationFailed", "NeedsManualReview"
# Longer than the slowest scenario: tok_capture_down parks after about 8 minutes.
$deadline = (Get-Date).AddMinutes(10)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 200
    try { $saga = Invoke-RestMethod -Uri "$Orders/orders/$id" } catch { continue }   # 404 until the first step commits

    # Oldest change first, so the output reads forward then backward like the saga ran.
    foreach ($j in $saga.journal | Sort-Object updatedUtc) {
        # The journal's Unknown means "command sent, no answer yet".
        $shown = if ($j.outcome -eq "Unknown") { "sent..." } else { $j.outcome }
        $line = "{0,-18} {1}" -f $j.step, $shown
        if ($seen.ContainsKey($line)) { continue }
        $seen[$line] = $true
        $color = switch ($shown) {
            "Succeeded"   { "Green" }
            "Rejected"    { "Red" }
            "Compensated" { "Yellow" }
            default       { "DarkGray" }
        }
        Write-Host "  $line" -ForegroundColor $color
    }

    if ($saga.status -in $final) {
        Write-Host ""
        $color = if ($saga.status -eq "Completed") { "Green" } else { "Magenta" }
        Write-Host "status: $($saga.status)" -ForegroundColor $color
        if ($saga.failureReason) { Write-Host "reason: $($saga.failureReason)" -ForegroundColor DarkGray }
        return
    }
}
Write-Host ""
Write-Host "still $($saga.status) after 10 minutes: GET $Orders/orders/$id" -ForegroundColor DarkGray
