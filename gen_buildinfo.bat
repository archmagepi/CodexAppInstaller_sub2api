@echo off
rem Generate BuildInfo.cs with the build timestamp (gitignored; csc needs it at compile time)
setlocal
cd /d "%~dp0"
for /f "tokens=1" %%a in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMddHHmm"') do set "BUILD_TIME=%%a"
> BuildInfo.cs echo internal static class BuildInfo
>> BuildInfo.cs echo {
>> BuildInfo.cs echo     public const string Time = "%BUILD_TIME%";
>> BuildInfo.cs echo }
endlocal
