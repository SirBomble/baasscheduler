#!/bin/pwsh
# Test script to verify custom config path works
$ErrorActionPreference = "Stop"

Write-Host "Testing Custom Configuration Path..." -ForegroundColor Green

# API endpoint (note: using port 5001 since that's what the custom config uses)
$baseUrl = "http://localhost:5001"
$password = "changeme"

try {
    # Step 1: Login
    Write-Host "1. Logging in..." -ForegroundColor Yellow
    $loginResponse = Invoke-RestMethod -Uri "$baseUrl/api/auth/login" -Method Post -Body (@{password=$password} | ConvertTo-Json) -ContentType "application/json"
    $sessionId = $loginResponse.sessionId
    $headers = @{"X-Session-Id" = $sessionId}
    Write-Host "   Logged in successfully" -ForegroundColor Green

    # Step 2: Get configuration file path
    Write-Host "2. Getting configuration file path..." -ForegroundColor Yellow
    $configPathResponse = Invoke-RestMethod -Uri "$baseUrl/api/config/path" -Method Get -Headers $headers
    $configPath = $configPathResponse.configurationFilePath
    Write-Host "   Configuration file: $configPath" -ForegroundColor Green
    
    # Verify it's using the custom config path
    $expectedPath = "C:\Users\Administrator\baasscheduler\custom-config.json"
    if ($configPath -eq $expectedPath) {
        Write-Host "   ✅ Using correct custom configuration path!" -ForegroundColor Green
    } else {
        Write-Host "   ❌ Expected: $expectedPath" -ForegroundColor Red
        Write-Host "   ❌ Actual: $configPath" -ForegroundColor Red
        exit 1
    }

    # Step 3: Check that the existing job from custom config is loaded
    Write-Host "3. Checking jobs from custom config..." -ForegroundColor Yellow
    $jobs = Invoke-RestMethod -Uri "$baseUrl/api/jobs" -Method Get -Headers $headers
    $customJob = $jobs | Where-Object { $_.name -eq "CustomConfigTest" }
    
    if ($customJob) {
        Write-Host "   ✅ Custom config job found: $($customJob.name)" -ForegroundColor Green
    } else {
        Write-Host "   ❌ Custom config job not found" -ForegroundColor Red
        Write-Host "   Available jobs: $($jobs | ConvertTo-Json)" -ForegroundColor Gray
        exit 1
    }

    # Step 4: Add a new job to test persistence
    Write-Host "4. Adding new job to test persistence..." -ForegroundColor Yellow
    $testJob = @{
        Name = "CustomConfigPersistenceTest"
        Schedule = "0 3 * * *"
        Script = "echo 'Testing custom config persistence'"
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

    # Step 5: Verify persistence
    Start-Sleep -Seconds 2
    Write-Host "5. Verifying job was persisted to custom config..." -ForegroundColor Yellow
    $configContent = Get-Content $configPath -Raw | ConvertFrom-Json
    $persistedJob = $configContent.Jobs | Where-Object { $_.Name -eq "CustomConfigPersistenceTest" }
    
    if ($persistedJob) {
        Write-Host "   ✅ Job persisted to custom configuration file!" -ForegroundColor Green
    } else {
        Write-Host "   ❌ Job not found in custom configuration file!" -ForegroundColor Red
        exit 1
    }

    # Step 6: Clean up
    Write-Host "6. Cleaning up..." -ForegroundColor Yellow
    Invoke-RestMethod -Uri "$baseUrl/api/jobs/CustomConfigPersistenceTest" -Method Delete -Headers $headers | Out-Null
    Write-Host "   Test job deleted" -ForegroundColor Green

    # Step 7: Logout
    Invoke-RestMethod -Uri "$baseUrl/api/auth/logout" -Method Post -Headers $headers | Out-Null
    Write-Host "7. Logged out successfully" -ForegroundColor Green

    Write-Host "`nCustom configuration path test completed successfully! ✅" -ForegroundColor Green

} catch {
    Write-Host "Test failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
