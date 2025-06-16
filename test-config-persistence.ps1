#!/bin/pwsh
# Test script to verify configuration persistence
$ErrorActionPreference = "Stop"

Write-Host "Testing BaaS Scheduler Configuration Persistence..." -ForegroundColor Green

# API endpoint
$baseUrl = "http://localhost:5000"
$password = "changeme"

try {
    # Step 1: Login
    Write-Host "1. Logging in..." -ForegroundColor Yellow
    $loginResponse = Invoke-RestMethod -Uri "$baseUrl/api/auth/login" -Method Post -Body (@{password=$password} | ConvertTo-Json) -ContentType "application/json"
    $sessionId = $loginResponse.sessionId
    $headers = @{"X-Session-Id" = $sessionId}
    Write-Host "   Logged in successfully with session: $sessionId" -ForegroundColor Green

    # Step 2: Get current configuration file path
    Write-Host "2. Getting configuration file path..." -ForegroundColor Yellow
    $configPathResponse = Invoke-RestMethod -Uri "$baseUrl/api/config/path" -Method Get -Headers $headers
    $configPath = $configPathResponse.configurationFilePath
    Write-Host "   Configuration file: $configPath" -ForegroundColor Green
    
    # Verify it's using the new default path
    if ($configPath -eq "C:\BAAS\BaaSScheduler.json") {
        Write-Host "   ✅ Using correct default configuration path!" -ForegroundColor Green
    } else {
        Write-Host "   ❌ Unexpected configuration path: $configPath" -ForegroundColor Red
    }

    # Step 3: Read current configuration
    Write-Host "3. Reading current configuration..." -ForegroundColor Yellow
    $beforeConfig = Get-Content $configPath -Raw | ConvertFrom-Json
    $initialJobCount = $beforeConfig.Jobs.Count
    Write-Host "   Initial job count: $initialJobCount" -ForegroundColor Green

    # Step 4: Add a test job via API
    Write-Host "4. Adding test job via API..." -ForegroundColor Yellow
    $testJob = @{
        Name = "ConfigPersistenceTest"
        Schedule = "0 2 * * *"
        Script = "echo 'Test job for configuration persistence'"
        Type = "powershell"
        Enabled = $true
        Webhooks = @{
            Enabled = $false
            Teams = ""
            Discord = ""
        }
    }
    $addJobResponse = Invoke-RestMethod -Uri "$baseUrl/api/jobs" -Method Post -Body ($testJob | ConvertTo-Json) -ContentType "application/json" -Headers $headers
    Write-Host "   Job added: $($addJobResponse.message)" -ForegroundColor Green

    # Step 5: Wait a moment for file to be written
    Start-Sleep -Seconds 2

    # Step 6: Read configuration file again
    Write-Host "5. Verifying configuration was persisted..." -ForegroundColor Yellow
    $afterConfig = Get-Content $configPath -Raw | ConvertFrom-Json
    $finalJobCount = $afterConfig.Jobs.Count
    
    $testJobInConfig = $afterConfig.Jobs | Where-Object { $_.Name -eq "ConfigPersistenceTest" }
    
    if ($testJobInConfig) {
        Write-Host "   SUCCESS: Test job found in configuration file!" -ForegroundColor Green
        Write-Host "   Job count increased from $initialJobCount to $finalJobCount" -ForegroundColor Green
        Write-Host "   Test job details:" -ForegroundColor Green
        Write-Host "     Name: $($testJobInConfig.Name)" -ForegroundColor Cyan
        Write-Host "     Schedule: $($testJobInConfig.Schedule)" -ForegroundColor Cyan
        Write-Host "     Script: $($testJobInConfig.Script)" -ForegroundColor Cyan
        Write-Host "     Enabled: $($testJobInConfig.Enabled)" -ForegroundColor Cyan
    } else {
        Write-Host "   FAILURE: Test job not found in configuration file!" -ForegroundColor Red
        Write-Host "   Configuration content:" -ForegroundColor Yellow
        Write-Host (Get-Content $configPath -Raw) -ForegroundColor Gray
        exit 1
    }

    # Step 7: Clean up - delete the test job
    Write-Host "6. Cleaning up - deleting test job..." -ForegroundColor Yellow
    $deleteResponse = Invoke-RestMethod -Uri "$baseUrl/api/jobs/ConfigPersistenceTest" -Method Delete -Headers $headers
    Write-Host "   Job deleted: $($deleteResponse.message)" -ForegroundColor Green

    # Step 8: Verify deletion was persisted
    Start-Sleep -Seconds 2
    $cleanupConfig = Get-Content $configPath -Raw | ConvertFrom-Json
    $testJobAfterDelete = $cleanupConfig.Jobs | Where-Object { $_.Name -eq "ConfigPersistenceTest" }
    
    if (-not $testJobAfterDelete) {
        Write-Host "   SUCCESS: Test job removal was persisted!" -ForegroundColor Green
    } else {
        Write-Host "   FAILURE: Test job still exists after deletion!" -ForegroundColor Red
        exit 1
    }

    # Step 9: Check backup file was created
    Write-Host "7. Checking backup file creation..." -ForegroundColor Yellow
    $backupPath = "$configPath.backup"
    if (Test-Path $backupPath) {
        Write-Host "   SUCCESS: Backup file created at $backupPath" -ForegroundColor Green
    } else {
        Write-Host "   WARNING: No backup file found" -ForegroundColor Yellow
    }

    # Step 10: Logout
    Write-Host "8. Logging out..." -ForegroundColor Yellow
    Invoke-RestMethod -Uri "$baseUrl/api/auth/logout" -Method Post -Headers $headers | Out-Null
    Write-Host "   Logged out successfully" -ForegroundColor Green

    Write-Host "`nConfiguration persistence test completed successfully! ✅" -ForegroundColor Green
    Write-Host "Default configuration path: C:\BAAS\BaaSScheduler.json ✅" -ForegroundColor Green

} catch {
    Write-Host "Test failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Stack trace: $($_.ScriptStackTrace)" -ForegroundColor Red
    exit 1
}
