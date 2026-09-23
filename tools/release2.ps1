# Re-publishes with the native libraries bundled INSIDE the single exe (that is how the previous
# 266 MB PokerVisionHUD.exe release was built - without it the app cannot load OpenCvSharpExtern).
# Then copies exe/tessdata/x64 into the release folder and rebuilds the release zip.

$log = 'E:\Build\release_log2.txt'
$release = 'E:\Poker Tools\PokerVisionHUD_v1.0'
$staging = 'E:\Build\PokerNoteManager\publish'
$proj = 'C:\Users\Neslo\source\repos\PokerNoteManager\PokerNoteManager\PokerNoteManager.csproj'

function Log([string]$msg) { "[$(Get-Date -Format 'HH:mm:ss')] $msg" | Add-Content -Path $log }
"=== release run 2 $(Get-Date -Format 'yyyyMMdd_HHmmss') ===" | Set-Content -Path $log

Log "publish started (single file + bundled natives)"
& dotnet publish $proj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $staging *>> $log
Log "publish exit code $LASTEXITCODE"
Log ("staged exe size: " + (Get-Item (Join-Path $staging 'PokerVisionHUD.exe')).Length)

$locked = $false
try {
    Copy-Item (Join-Path $staging 'PokerVisionHUD.exe') (Join-Path $release 'PokerVisionHUD.exe') -Force
    Log "exe copied"
} catch { $locked = $true; Log "ERROR exe copy failed (app running?): $($_.Exception.Message)" }

# tessdata + x64: the publish output only holds them when the nuget package copies them there. The
# exe already bundles both (verified: Tesseract initialises without the folders), but the folder
# layout stays identical to the older releases, so fall back to the project / nuget cache.
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
        # (tessdata\tessdata), which doubled the native payload inside the previous release zip.
        if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
        Copy-Item $src $dst -Recurse -Force
        $n = (Get-ChildItem $dst -Recurse -File | Measure-Object -Sum Length)
        Log "$sub copied ($($n.Count) file(s), $($n.Sum) bytes) from $src"
    } catch { Log "WARN $sub copy failed: $($_.Exception.Message)" }
}

if ($locked) {
    Log "SKIPPED zip (exe was locked)"
} else {
    # ZipFile.CreateFromDirectory, not Compress-Archive: Compress-Archive silently left the
    # tessdata / x64 sub folders out of the archive (the zip then only held the exe + README).
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Remove-Item 'E:\Poker Tools\PokerVisionHUD_v1.0.zip' -Force -ErrorAction SilentlyContinue
    Log "zip started"
    try {
        [System.IO.Compression.ZipFile]::CreateFromDirectory($release, 'E:\Poker Tools\PokerVisionHUD_v1.0.zip', [System.IO.Compression.CompressionLevel]::Optimal, $false)
        Log ("zip done: " + (Get-Item 'E:\Poker Tools\PokerVisionHUD_v1.0.zip').Length + " bytes")
        $z = [System.IO.Compression.ZipFile]::OpenRead('E:\Poker Tools\PokerVisionHUD_v1.0.zip')
        Log ("entries: " + (($z.Entries | ForEach-Object { $_.FullName }) -join ', '))
        $z.Dispose()
    } catch { Log "ERROR zip failed: $($_.Exception.Message)" }
}

Log "DONE"
