@echo off
cd /d "%~dp0"
dotnet run --project "WindUpRelay.Host\WindUpRelay.Host.csproj" -c Release --no-launch-profile
