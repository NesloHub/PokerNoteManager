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


    static string MediumBlobs(string title)
    {
        var sb = new StringBuilder();
        sb.AppendLine("-- " + title + " --");
        var list = Blobs((b, g, r) => ((r + g + b) / 3) >= 130, 40);
        list.Sort((a, b) => b[4].CompareTo(a[4]));
        int shown = 0;
        foreach (var bl in list)
        {
            if (bl[2] < W * 0.009 || bl[2] > W * 0.030) continue;
            if (bl[3] < H * 0.012 || bl[3] > H * 0.055) continue;
            double asp = bl[2] / (double)bl[3];
            if (asp < 0.7 || asp > 1.4) continue;
            double solid = bl[4] / (double)(bl[2] * bl[3]);
            if (solid < 0.35 || solid > 0.95) continue;
            int dark = 0, tot = 0;
            for (int y = bl[1]; y < bl[1] + bl[3]; y++)
                for (int x = bl[0]; x < bl[0] + bl[2]; x++)
                {
                    int i = y * STR + x * 4;
                    int gray = (px[i] * 114 + px[i + 1] * 587 + px[i + 2] * 299) / 1000;
                    tot++; if (gray < 100) dark++;
                }
            double darkFrac = dark / (double)Math.Max(1, tot);
            if (darkFrac < 0.06 || darkFrac > 0.55) continue;
            sb.AppendLine(string.Format("  x={0} y={1} w={2} h={3} solid={4:F2} rgb={5},{6},{7} darkFrac={8:F2} (x/W={9:F3} y/H={10:F3})",
                bl[0], bl[1], bl[2], bl[3], solid, bl[5], bl[6], bl[7], darkFrac, bl[0] / (double)W, bl[1] / (double)H));
            if (++shown > 25) break;
        }
        return sb.ToString();
    }

    public static string Analyze(string path)
    {
        Load(path);
        var sb = new StringBuilder();
        sb.AppendLine("===============================================================");
        sb.AppendLine("== " + System.IO.Path.GetFileName(path) + "  " + W + "x" + H);
        sb.Append(MediumBlobs("RING candidates (light blob with dark core, badge size)"));
        return sb.ToString();
    }
}
'@

Add-Type -TypeDefinition $src -ReferencedAssemblies 'System.Drawing'

$out = 'C:\Users\Neslo\source\repos\PokerNoteManager\tools\shots_rings.txt'
$sb = New-Object System.Text.StringBuilder
foreach ($p in $Paths)
{
    if (-not (Test-Path $p)) { Write-Host "MISSING: $p"; continue }
    [void]$sb.AppendLine([ShotZones]::Analyze($p))
}
$sb.ToString() | Out-File -FilePath $out -Encoding UTF8
Write-Host "written: $out"
