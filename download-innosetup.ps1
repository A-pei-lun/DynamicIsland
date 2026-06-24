$url = 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe'
$out = 'E:\VS Studio Programs\DynamicIsland\innosetup-6.7.3.exe'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Write-Host "Downloading Inno Setup 6.7.3..."
Invoke-WebRequest -Uri $url -OutFile $out -UseBasicParsing
Write-Host "Downloaded: $((Get-Item $out).Length) bytes"
