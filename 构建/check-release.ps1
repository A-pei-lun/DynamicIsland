$releases = Invoke-RestMethod -Uri 'https://api.github.com/repos/jrsoftware/issrc/releases?per_page=10' -UseBasicParsing
$releases | ForEach-Object {
    Write-Host "=== $($_.tag_name) ==="
    $_.assets | Select-Object name, browser_download_url | Format-Table -AutoSize
}
