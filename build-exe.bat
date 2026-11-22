@echo off
setlocal
rem Build CodexAppInstaller.exe from CodexAppInstaller.cs (self-contained C#)
rem Requires .NET Framework 4.x compiler + PowerShell SDK (both ship with Windows).
rem Extra refs: System.IO.Compression(.FileSystem) for zip extract,
rem             System.Web.Extensions for JavaScriptSerializer (JSON parsing).
rem After building, packages the exe into a password-encrypted zip (7-Zip).

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [ERROR] csc.exe not found. .NET Framework 4.x required.
    exit /b 1
)

set "PSDLL=%WINDIR%\System32\WindowsPowerShell\v1.0\System.Management.Automation.dll"
if not exist "%PSDLL%" set "PSDLL=%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System.Management.Automation\v4.0_3.0.0.0__31bf3856ad364e35\System.Management.Automation.dll"
if not exist "%PSDLL%" (
    echo [ERROR] System.Management.Automation.dll not found.
    exit /b 1
)

set "ZIP_OUT=CodexAppInstaller.zip"

set "SEVENZIP=%ProgramFiles%\7-Zip\7z.exe"
if not exist "%SEVENZIP%" set "SEVENZIP=%ProgramFiles(x86)%\7-Zip\7z.exe"
if not exist "%SEVENZIP%" for %%I in (7z.exe) do set "SEVENZIP=%%~$PATH:I"

cd /d "%~dp0"
call gen_buildinfo.bat
"%CSC%" /nologo /target:exe /optimize /platform:x64 /win32icon:xode-logo.ico /out:CodexAppInstaller.exe /r:"%PSDLL%" /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll CodexAppInstaller.cs BuildInfo.cs
if errorlevel 1 (
    echo [ERROR] compile failed.
    exit /b 1
)
echo [OK] CodexAppInstaller.exe built.

rem ===== 打包为 zip（分发产物） =====
if not exist "%SEVENZIP%" (
    echo [ERROR] 未找到 7-Zip（需要它生成 zip）。请安装 7-Zip 或将其加入 PATH。
    echo [OK] CodexAppInstaller.exe 已构建，但未打包 %ZIP_OUT%。
    exit /b 1
)
if exist "%ZIP_OUT%" del /f /q "%ZIP_OUT%"
"%SEVENZIP%" a -tzip -y "%ZIP_OUT%" "CodexAppInstaller.exe" >nul
if errorlevel 1 (
    echo [ERROR] 打包失败。
    exit /b 1
)
rem 自检：完整解压测试
"%SEVENZIP%" t -y "%ZIP_OUT%" >nul
if errorlevel 1 (
    echo [ERROR] zip 自检失败（完整性异常）。
    exit /b 1
)
for %%F in ("%ZIP_OUT%") do echo [OK] %ZIP_OUT% built (%%~zF bytes).
endlocal
