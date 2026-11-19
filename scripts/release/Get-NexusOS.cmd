@echo off
rem Get NexusOS: double-click to download the newest NexusOS installer image into Downloads,
rem check it and join its parts. It runs scripts/release/get-nexusos.ps1 from the NexusOS repository.
powershell -NoProfile -ExecutionPolicy Bypass -Command "irm https://raw.githubusercontent.com/TheMatthewWalker/NexusOS/main/scripts/release/get-nexusos.ps1 | iex"
echo.
pause
