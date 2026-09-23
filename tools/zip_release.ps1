# Rebuilds the release zip from the release folder. Uses ZipFile.CreateFromDirectory (not
# Compress-Archive: that one dropped the tessdata/x64 sub folders here and produced a zip that
# only contained the exe and the README).
# Run: powershell -ExecutionPolicy Bypass -File tools\zip_release.ps1

$release = 'E:\Poker Tools\PokerVisionHUD_v1.0'
$zip = 'E:\Poker Tools\PokerVisionHUD_v1.0.zip'
$log = 'E:\Build\zip_log.txt'

function Log([string]$m) { "[$(Get-Date -Format 'HH:mm:ss')] $m" | Add-Content -Path $log }
"=== zip run $(Get-Date -Format 'yyyyMMdd_HHmmss') ===" | Set-Content -Path $log

Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path $zip) { Remove-Item $zip -Force }
try {
    [System.IO.Compression.ZipFile]::CreateFromDirectory($release, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    $fi = Get-Item $zip
    Log ("zip done: " + $fi.Length + " bytes")
    $z = [System.IO.Compression.ZipFile]::OpenRead($zip)
    Log ("entries: " + ($z.Entries | ForEach-Object { $_.FullName + '(' + $_.Length + ')' }) -join ', ')
    $z.Dispose()
} catch {
    Log ("ERROR zip failed: " + $_.Exception.Message)
}
Log "DONE"
