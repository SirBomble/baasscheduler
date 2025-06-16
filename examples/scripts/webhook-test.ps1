Write-Output "=== BaaS Scheduler Test Script ==="
Write-Output "Script started at: $(Get-Date)"
Write-Output "Current user: $env:USERNAME"
Write-Output "Current directory: $(Get-Location)"
Write-Output ""

Write-Output "=== System Information ==="
Write-Output "Computer name: $env:COMPUTERNAME"
Write-Output "OS Version: $(Get-CimInstance Win32_OperatingSystem | Select-Object -ExpandProperty Caption)"
Write-Output "PowerShell version: $($PSVersionTable.PSVersion)"
Write-Output ""

Write-Output "=== Disk Space ==="
Get-CimInstance -ClassName Win32_LogicalDisk | Where-Object {$_.DriveType -eq 3} | ForEach-Object {
    $freeGB = [math]::Round($_.FreeSpace / 1GB, 2)
    $sizeGB = [math]::Round($_.Size / 1GB, 2)
    $usedPercent = [math]::Round((($_.Size - $_.FreeSpace) / $_.Size) * 100, 1)
    Write-Output "Drive $($_.DeviceID) - Free: ${freeGB}GB / Total: ${sizeGB}GB (${usedPercent}% used)"
}
Write-Output ""

Write-Output "=== Running some operations ==="
Write-Output "Generating random numbers..."
for ($i = 1; $i -le 5; $i++) {
    $randomNum = Get-Random -Minimum 1 -Maximum 1000
    Write-Output "Random number $i: $randomNum"
    Start-Sleep -Milliseconds 200
}
Write-Output ""

Write-Output "=== Process Information ==="
$processes = Get-Process | Sort-Object CPU -Descending | Select-Object -First 5
Write-Output "Top 5 processes by CPU usage:"
$processes | ForEach-Object {
    $cpu = if ($_.CPU) { [math]::Round($_.CPU, 2) } else { "0" }
    Write-Output "  $($_.ProcessName) (PID: $($_.Id)) - CPU: $cpu"
}
Write-Output ""

Write-Output "=== Memory Usage ==="
$memory = Get-CimInstance Win32_OperatingSystem
$totalMemGB = [math]::Round($memory.TotalVisibleMemorySize / 1MB, 2)
$freeMemGB = [math]::Round($memory.FreePhysicalMemory / 1MB, 2)
$usedMemGB = [math]::Round($totalMemGB - $freeMemGB, 2)
$memUsedPercent = [math]::Round(($usedMemGB / $totalMemGB) * 100, 1)
Write-Output "Total Memory: ${totalMemGB}GB"
Write-Output "Used Memory: ${usedMemGB}GB (${memUsedPercent}%)"
Write-Output "Free Memory: ${freeMemGB}GB"
Write-Output ""

# Simulate some work
Write-Output "=== Simulating work ==="
Write-Output "Processing items..."
for ($i = 1; $i -le 3; $i++) {
    Write-Output "Processing item $i/3..."
    Start-Sleep -Seconds 1
}
Write-Output "Work completed successfully!"
Write-Output ""

Write-Output "=== Script Completed ==="
Write-Output "Script finished at: $(Get-Date)"
Write-Output "Total execution time: $((Get-Date) - (Get-Process -Id $PID).StartTime)"
Write-Output "Exit code: 0"

exit 0
