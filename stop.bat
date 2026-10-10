@echo off
rem Stop the FAKE ESP background service. ASCII-only content on purpose:
rem cmd parses .bat bytes in the console codepage, so non-ASCII here can break parsing.
powershell -NoProfile -ExecutionPolicy Bypass -Command "try { $r = Invoke-WebRequest 'http://127.0.0.1:17777/shutdown' -UseBasicParsing -TimeoutSec 3; Write-Host 'stopped:' $r.Content } catch { Write-Host 'FAKE ESP is not running' }"
timeout /t 2 >nul 2>&1
