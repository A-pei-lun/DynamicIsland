$installer = "E:\VS Studio Programs\DynamicIsland\innosetup-6.7.3.exe"
$targetDir = "$env:LOCALAPPDATA\Programs\Inno Setup 6"

Write-Host "Target: $targetDir"
New-Item -ItemType Directory -Path $targetDir -Force | Out-Null

Write-Host "Extracting Inno Setup 6.7.3..."
Start-Process -FilePath $installer -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR=`"$targetDir`"" -Wait -NoNewWindow

Write-Host "Waiting..."
Start-Sleep -Seconds 3

$iscc = Get-ChildItem -Path $targetDir -Recurse -Filter "ISCC.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($iscc) {
    Write-Host "ISCC.exe found at: $($iscc.FullName)"
    Write-Host "Files:"
    Get-ChildItem -Path $targetDir | Select-Object Name, Length | Format-Table -AutoSize
} else {
    Write-Host "Not found. Checking all possible locations..."
    @(
        $targetDir
        "C:\Program Files (x86)\Inno Setup 6"
        "$env:LOCALAPPDATA\Inno Setup 6"
        "$env:ProgramData\Inno Setup 6"
        "$env:TEMP\is-*.tmp"
    ) | ForEach-Object {
        $p = $_
        if (Test-Path $p) {
            Write-Host "Contents of $p :"
            Get-ChildItem -Path $p -ErrorAction SilentlyContinue | Select-Object FullName | Format-Table -AutoSize
        }
    }

    # Try /EXTRACT mode
    Write-Host "`nTrying /EXTRACT mode..."
    Start-Process -FilePath $installer -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /DIR=`"$targetDir`" /EXTRACT" -Wait -NoNewWindow
    Start-Sleep -Seconds 3
    Get-ChildItem -Path $targetDir -Recurse -ErrorAction SilentlyContinue | Select-Object FullName, Length | Format-Table -AutoSize
}
