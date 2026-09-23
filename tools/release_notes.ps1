# Publishes Poker Notes (WPF only, single file, self contained) and refreshes the release folder
# and the release zip. The old HUD build of the app is kept in E:\Poker Tools\Backups\ as
# PokerVisionHUD_HUD_build_*.exe, so the tracker version can be restored if ever needed.
# Run: powershell -ExecutionPolicy Bypass -File tools\release_notes.ps1

$log = 'E:\Build\release_notes.txt'
$release = 'E:\Poker Tools\PokerVisionHUD_v1.0'
$zip = 'E:\Poker Tools\PokerVisionHUD_v1.0.zip'
$staging = 'E:\Build\PokerNoteManager\publish_notes'
$proj = 'C:\Users\Neslo\source\repos\PokerNoteManager\PokerNoteManager\PokerNoteManager.csproj'

function Log([string]$m) { "[$(Get-Date -Format 'HH:mm:ss')] $m" | Add-Content -Path $log }
"=== release run $(Get-Date -Format 'yyyyMMdd_HHmmss') ===" | Set-Content -Path $log

Log "publish started"
& dotnet publish $proj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $staging *>> $log
Log "publish exit code $LASTEXITCODE"

$exe = Join-Path $staging 'PokerVisionHUD.exe'
if (-not (Test-Path $exe)) { Log "ERROR exe not found in staging"; Log "DONE"; exit 1 }
Log ("staged exe size: " + (Get-Item $exe).Length)

$locked = $false
try
{
    # Copy every file the publish produced next to the exe (the exe itself plus side by side
    # assemblies such as Tesseract.dll, which must NOT be bundled - see the csproj target).
    foreach ($file in Get-ChildItem $staging -File)
    {
        Copy-Item $file.FullName (Join-Path $release $file.Name) -Force
    }
    Log ("files copied: " + ((Get-ChildItem $staging -File | ForEach-Object { $_.Name }) -join ', '))
}
catch { $locked = $true; Log "ERROR exe copy failed (app running?): $($_.Exception.Message)" }

# The capture feature needs the OCR language data and the Tesseract native DLLs next to the exe.
# They are copied from the publish output, with a fall back to the project / the nuget cache.
$tessSrc = Join-Path $staging 'tessdata'
if (-not (Test-Path (Join-Path $tessSrc 'eng.traineddata'))) {
    $tessSrc = 'C:\Users\Neslo\source\repos\PokerNoteManager\PokerNoteManager\tessdata'
}
$x64Src = Join-Path $staging 'x64'
if (-not (Get-ChildItem (Join-Path $x64Src '*.dll') -ErrorAction SilentlyContinue)) {
    $x64Src = Join-Path $env:USERPROFILE '.nuget\packages\tesseract\5.2.0\x64'
}

foreach ($sub in 'tessdata', 'x64') {
    try {
        $src = if ($sub -eq 'tessdata') { $tessSrc } else { $x64Src }
        $dst = Join-Path $release $sub
        # Delete the destination first: copying a folder into an existing folder nests it
        # (tessdata\tessdata), which doubled the native payload inside an earlier release zip.
        if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
        Copy-Item $src $dst -Recurse -Force
        $n = Get-ChildItem $dst -Recurse -File | Measure-Object -Sum Length
        Log "$sub copied ($($n.Count) file(s), $($n.Sum) bytes)"
    } catch { Log "WARN $sub copy failed: $($_.Exception.Message)" }
}

if ($locked)
{
    Log "SKIPPED zip (exe was locked)"
}
else
{
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path $zip) { Copy-Item $zip ("E:\Poker Tools\Backups\PokerVisionHUD_v1.0_" + (Get-Date -Format 'yyyyMMdd_HHmmss') + ".zip") -Force }
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Log "zip started"
    try
    {
        [System.IO.Compression.ZipFile]::CreateFromDirectory($release, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
        Log ("zip done: " + (Get-Item $zip).Length + " bytes")
        $z = [System.IO.Compression.ZipFile]::OpenRead($zip)
        Log ("entries: " + (($z.Entries | ForEach-Object { $_.FullName }) -join ', '))
        $z.Dispose()
    }
    catch { Log "ERROR zip failed: $($_.Exception.Message)" }
}

Log "DONE"
