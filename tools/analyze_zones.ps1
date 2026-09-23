param([string[]]$Paths = @(
  'C:\Users\Neslo\Pictures\fullscreen.png',
  'C:\Users\Neslo\Pictures\2tile.png',
  'C:\Users\Neslo\Pictures\9tile.png'
))

Add-Type -AssemblyName System.Drawing

$src = @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

public static class ShotZones
{
    static byte[] px; static int W, H, STR;

    static void Load(string path)
    {
        using (var bmp = new Bitmap(path))
        {
            W = bmp.Width; H = bmp.Height;
            var bd = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            STR = bd.Stride;
            px = new byte[STR * H];
            Marshal.Copy(bd.Scan0, px, 0, px.Length);
            bmp.UnlockBits(bd);
        }
    }

    static bool IsFelt(int b, int g, int r) { return g > 55 && (g - r) > 18 && (g - b) > 8; }
    static bool IsBright(int b, int g, int r) { return (r + g + b) > 600; }
    static bool IsGreenTxt(int b, int g, int r) { return g > 120 && (g - r) > 10 && (g - b) > 30; }
    static bool IsWhiteTxt(int b, int g, int r)
    {
        int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
        return (r + g + b) > 480 && (mx - mn) < 45;
    }

    static List<int[]> Blobs(Func<int, int, int, bool> test, int minArea)
    {
        bool[] mask = new bool[W * H];
        for (int y = 0; y < H; y++) { int off = y * STR; for (int x = 0; x < W; x++) { int i = off + x * 4; if (test(px[i], px[i + 1], px[i + 2])) mask[y * W + x] = true; } }
        bool[] seen = new bool[W * H]; int[] stack = new int[W * H];
        var res = new List<int[]>();
        for (int idx = 0; idx < mask.Length; idx++)
        {
            if (!mask[idx] || seen[idx]) continue;
            int sp = 0; stack[sp++] = idx; seen[idx] = true;
            int mnx = W, mny = H, mxx = -1, mxy = -1, area = 0; long sr = 0, sg = 0, sb = 0;
            while (sp > 0)
            {
                int cur = stack[--sp]; int cy = cur / W, cx = cur - cy * W; area++;
                if (cx < mnx) mnx = cx; if (cx > mxx) mxx = cx; if (cy < mny) mny = cy; if (cy > mxy) mxy = cy;
                int ii = cy * STR + cx * 4; sb += px[ii]; sg += px[ii + 1]; sr += px[ii + 2];
                if (cx > 0 && mask[cur - 1] && !seen[cur - 1]) { seen[cur - 1] = true; stack[sp++] = cur - 1; }
                if (cx < W - 1 && mask[cur + 1] && !seen[cur + 1]) { seen[cur + 1] = true; stack[sp++] = cur + 1; }
                if (cy > 0 && mask[cur - W] && !seen[cur - W]) { seen[cur - W] = true; stack[sp++] = cur - W; }
                if (cy < H - 1 && mask[cur + W] && !seen[cur + W]) { seen[cur + W] = true; stack[sp++] = cur + W; }
            }
            if (area < minArea) continue;
            res.Add(new int[] { mnx, mny, mxx - mnx + 1, mxy - mny + 1, area,
                                (int)(sr / area), (int)(sg / area), (int)(sb / area) });
        }
        return res;
    }

    static string TextRows(List<int[]> blobs, string title)
    {
        // group blobs into text lines by vertical overlap
        var sorted = new List<int[]>(blobs);
        sorted.Sort((a, b) => (a[1] + a[3] / 2).CompareTo(b[1] + b[3] / 2));
        var lines = new List<int[]>();
        foreach (var bl in sorted)
        {
            bool placed = false;
            foreach (var ln in lines)
            {
                int c1 = bl[1] + bl[3] / 2, c2 = ln[1] + ln[3] / 2;
                if (Math.Abs(c1 - c2) <= Math.Max(6, (bl[3] + ln[3]) / 3))
                {
                    ln[0] = Math.Min(ln[0], bl[0]);
                    ln[1] = Math.Min(ln[1], bl[1]);
                    ln[2] = Math.Max(ln[2], bl[0] + bl[2]);
                    ln[3] = Math.Max(ln[3], bl[1] + bl[3]);
                    ln[4] += bl[4]; ln[5]++;
                    placed = true; break;
                }
            }
            if (!placed) lines.Add(new int[] { bl[0], bl[1], bl[0] + bl[2], bl[1] + bl[3], bl[4], 1, bl[1] });
        }
        lines.Sort((a, b) => a[1].CompareTo(b[1]));
        var sb = new StringBuilder();
        sb.AppendLine("-- " + title + " --");
        foreach (var ln in lines)
        {
            if (ln[5] < 3) continue;   // need at least 3 glyph blobs to be a text run
            sb.AppendLine(string.Format("  row y={0}..{1} x={2}..{3} blobs={4} area={5}  (y/H={6:F3}..{7:F3}, x/W={8:F3}..{9:F3})",
                ln[1], ln[3], ln[0], ln[2], ln[5], ln[4],
                ln[1] / (double)H, ln[3] / (double)H, ln[0] / (double)W, ln[2] / (double)W));
        }
        return sb.ToString();
    }

    static string Circles(int minSizeFrac, int maxSizeFrac, string title)
    {
        var sb = new StringBuilder();
        sb.AppendLine("-- " + title + " --");
        int minW = (int)(W * minSizeFrac / 1000.0), maxW = (int)(W * maxSizeFrac / 1000.0);
        foreach (var bl in Blobs(IsBright, 40))
        {
            if (bl[2] < minW || bl[2] > maxW || bl[3] < minW || bl[3] > maxW) continue;
            double asp = bl[2] / (double)bl[3];
            if (asp < 0.75 || asp > 1.35) continue;
            int dark = 0, tot = 0;
            for (int y = bl[1]; y < bl[1] + bl[3]; y++)
                for (int x = bl[0]; x < bl[0] + bl[2]; x++)
                {
                    int i = y * STR + x * 4;
                    int gray = (px[i] * 114 + px[i + 1] * 587 + px[i + 2] * 299) / 1000;
                    tot++;
                    if (gray < 100) dark++;
                }
            double darkFrac = dark / (double)Math.Max(1, tot);
            if (darkFrac < 0.05 || darkFrac > 0.6) continue;
            sb.AppendLine(string.Format("  cand x={0} y={1} w={2} h={3} area={4} rgb={5},{6},{7} darkFrac={8:F2} (x/W={9:F3} y/H={10:F3})",
                bl[0], bl[1], bl[2], bl[3], bl[4], bl[5], bl[6], bl[7], darkFrac, bl[0] / (double)W, bl[1] / (double)H));
        }
        return sb.ToString();
    }

    public static string Analyze(string path)
    {
        Load(path);
        var sb = new StringBuilder();
        sb.AppendLine("===============================================================");
        sb.AppendLine("== " + System.IO.Path.GetFileName(path) + "  " + W + "x" + H);

        int[] felt = new int[] { 0, 0, W - 1, H - 1, 0 };
        foreach (var bl in Blobs(IsFelt, 2000))
            if (bl[4] > felt[4]) felt = new int[] { bl[0], bl[1], bl[0] + bl[2] - 1, bl[1] + bl[3] - 1, bl[4] };
        sb.AppendLine(string.Format("FELT x={0} y={1} w={2} h={3} area={4} | x/W={5:F3}..{6:F3}  y/H={7:F3}..{8:F3}",
            felt[0], felt[1], felt[2] - felt[0] + 1, felt[3] - felt[1] + 1, felt[4],
            felt[0] / (double)W, felt[2] / (double)W, felt[1] / (double)H, felt[3] / (double)H));

        sb.Append(TextRows(Blobs(IsWhiteTxt, 30), "WHITE TEXT rows"));
        sb.Append(TextRows(Blobs(IsGreenTxt, 25), "GREEN TEXT rows"));
        sb.Append(Circles(9, 40, "ROUND bright blobs with dark core (dealer button candidates)"));
        return sb.ToString();
    }
}
'@

Add-Type -TypeDefinition $src -ReferencedAssemblies 'System.Drawing'

$out = 'C:\Users\Neslo\source\repos\PokerNoteManager\tools\shots_zones.txt'
$sb = New-Object System.Text.StringBuilder
foreach ($p in $Paths)
{
    if (-not (Test-Path $p)) { Write-Host "MISSING: $p"; continue }
    [void]$sb.AppendLine([ShotZones]::Analyze($p))
}
$sb.ToString() | Out-File -FilePath $out -Encoding UTF8
Write-Host "written: $out"
