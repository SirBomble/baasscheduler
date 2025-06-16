Write-Output "=== BaaS Scheduler Failure Test Script ==="
Write-Output "Script started at: $(Get-Date)"
Write-Output ""

Write-Output "=== Performing some operations before failure ==="
Write-Output "Step 1: Checking system status..."
Write-Output "System check: OK"
Start-Sleep -Seconds 1

Write-Output "Step 2: Loading configuration..."
Write-Output "Configuration loaded: OK"
Start-Sleep -Seconds 1

Write-Output "Step 3: Processing data..."
Write-Output "Processing 100 items..."
for ($i = 1; $i -le 5; $i++) {
    Write-Output "Processed item $($i * 20)/100"
    Start-Sleep -Milliseconds 500
}

Write-Output ""
Write-Output "=== Error Simulation ==="
Write-Error "Critical error occurred: Database connection failed!"
Write-Output "Error details: Connection timeout after 30 seconds"
Write-Output "Stack trace: at ProcessData() line 42"
Write-Output "Attempted retry 3 times, all failed"
Write-Output ""

Write-Output "=== Cleanup Operations ==="
Write-Output "Performing cleanup..."
Write-Output "Temporary files removed"
Write-Output "Connections closed"
Write-Output ""

Write-Output "Script finished at: $(Get-Date)"
Write-Output "Exit code: 1"

exit 1
