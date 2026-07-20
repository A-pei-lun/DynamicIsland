@echo off
REM Extract Inno Setup 6 portable
mkdir "C:\Program Files (x86)\Inno Setup 6" 2>nul
"E:\VS Studio Programs\DynamicIsland\innosetup-6.7.3.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR="C:\Program Files (x86)\Inno Setup 6" /NOICONS /COMPONENTS="comp_main,comp_unicode"
echo EXIT: %errorlevel%
if exist "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" (
    echo ISCC.exe found!
    dir "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
) else (
    echo ISCC.exe NOT found, trying /EXTRACT...
    mkdir "C:\Program Files (x86)\Inno Setup 6" 2>nul
    "E:\VS Studio Programs\DynamicIsland\innosetup-6.7.3.exe" /VERYSILENT /SUPPRESSMSGBOXES /DIR="C:\Program Files (x86)\Inno Setup 6" /EXTRACT
    echo EXTRACT EXIT: %errorlevel%
    dir "C:\Program Files (x86)\Inno Setup 6" /b
)
