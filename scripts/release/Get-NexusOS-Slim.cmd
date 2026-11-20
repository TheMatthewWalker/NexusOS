@echo off
rem Get NexusOS (slim): double-click to download the smaller NexusOS installer image, without the
rem local AI model (it downloads once NexusOS is online), into Downloads, check it and join its parts.
powershell -NoProfile -ExecutionPolicy Bypass -Command "$env:NEXUSOS_EDITION='slim'; irm https://raw.githubusercontent.com/TheMatthewWalker/NexusOS/main/scripts/release/get-nexusos.ps1 | iex"
echo.
pause
