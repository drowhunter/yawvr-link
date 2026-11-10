@echo off
dotnet publish "%~dp0src\YawVR.Link\YawVR.Link.csproj" -c Debug -p:PublishProfile=Development %*
exit /b %errorlevel%
