# End-to-end validation of the felt relative zones: the felt rects below are the ones the real
# detector returns for the three Unibet screenshots (see verify_felt.txt), the element rects are
# measured from the screenshots (shots_zones.txt). Everything is in tile relative pixels.
# Run: powershell -ExecutionPolicy Bypass -File tools\validate_zones.ps1

$cases = @(
    @{ Name = 'fullscreen (2050x1184 tile, flop)'; Felt = @(369, 346, 1314, 589);
       Pot = @(978, 413, 196, 29); Board = @(749, 475, 283, 56); Badge = @(438, 677, 58, 50) },
    @{ Name = '2-tile (1266x743 tile, turn)'; Felt = @(228, 226, 812, 300);
       Pot = @(703, 267, 27, 17); Board = @(457, 305, 248, 35); Badge = @(525, 458, 35, 31) },
    @{ Name = '9-tile (837x502 tile, preflop)'; Felt = @(151, 160, 537, 198);
       Pot = @(459, 187, 28, 11); Board = $null; Badge = @(297, 336, 26, 30) }
)

function Box([int[]]$f, [double]$x0, [double]$y0, [double]$w, [double]$h) {
    @([int]($f[0] + $f[2] * $x0), [int]($f[1] + $f[3] * $y0), [int]($f[2] * $w), [int]($f[3] * $h))
}
function Contains([int[]]$outer, [int[]]$inner) {
    $inner[0] -ge $outer[0] -and $inner[1] -ge $outer[1] -and
    ($inner[0] + $inner[2]) -le ($outer[0] + $outer[2]) -and
    ($inner[1] + $inner[3]) -le ($outer[1] + $outer[3])
}
function Report([string]$what, [bool]$ok) {
    Write-Host ("   {0,-34} {1}" -f $what, $(if ($ok) { 'PASS' } else { 'FAIL' }))
}

$fail = 0
foreach ($c in $cases) {
    $f = $c.Felt
    $potBand = Box $f 0.10 0.00 0.80 0.21
    $boardBand = Box $f 0.15 0.19 0.70 0.27
    Write-Host "== $($c.Name)"
    Write-Host ("   felt      = {0},{1} {2}x{3}" -f $f[0], $f[1], $f[2], $f[3])
    Write-Host ("   pot band  = {0},{1} {2}x{3}" -f $potBand[0], $potBand[1], $potBand[2], $potBand[3])
    $ok = Contains $potBand $c.Pot
    if (-not $ok) { $fail++ }
    Report 'pot text inside pot band' $ok

    Write-Host ("   board band= {0},{1} {2}x{3}" -f $boardBand[0], $boardBand[1], $boardBand[2], $boardBand[3])
    if ($c.Board) {
        $ok = Contains $boardBand $c.Board
        if (-not $ok) { $fail++ }
        Report 'board cards inside board band' $ok
        $ok = -not (Contains $boardBand $c.Pot)
        if (-not $ok) { $fail++ }
        Report 'pot text outside board band' $ok
    } else {
        Write-Host "   board cards                        n/a (preflop frame)"
    }
    $ok = -not (Contains $boardBand $c.Badge)
    if (-not $ok) { $fail++ }
    Report 'dealer badge outside board band' $ok
    Write-Host ""
}
Write-Host ("RESULT: {0}" -f $(if ($fail -eq 0) { 'ALL PASS' } else { "$fail FAILED CHECK(S)" }))
