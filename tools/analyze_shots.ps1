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

public static class ShotAnalyzer
{
    private static byte[] Load(string path, out int w, out int h, out int stride)
    {
        using (var bmp = new Bitmap(path))
        {
            w = bmp.Width; h = bmp.Height;
            var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            stride = bd.Stride;
            byte[] px = new byte[stride * h];
            Marshal.Copy(bd.Scan0, px, 0, px.Length);
            bmp.UnlockBits(bd);
            return px;
        }
    }

    private static bool Test(string kind, int b, int g, int r)
    {
        switch (kind)
        {
            case "felt": return g > 55 && (g - r) > 18 && (g - b) > 8;
            case "bright": return (r + g + b) > 600;
            case "lgreen": return g > 120 && (g - b) > 30 && (g - r) > 10;
            case "red": return r > 120 && (r - g) > 45 && (r - b) > 45;
        }
        return false;
    }

    public static string Analyze(string path)
    {
        int w, h, stride;
        byte[] px = Load(path, out w, out h, out stride);
        var sb = new StringBuilder();
        sb.AppendLine("=================================================================");
        sb.AppendLine("== " + System.IO.Path.GetFileName(path) + "   " + w + "x" + h);

        int[] colCount = new int[w];
        int[] rowCount = new int[h];
        int minX = w, maxX = -1, minY = h, maxY = -1;
        for (int y = 0; y < h; y++)
        {
            int off = y * stride;
            for (int x = 0; x < w; x++)
            {
                int i = off + x * 4;
                if ((px[i] + px[i + 1] + px[i + 2]) > 45)
                {
                    colCount[x]++; rowCount[y]++;
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
            }
        }
        int rightEdge = -1, bottomEdge = -1;
        for (int x = w - 1; x >= 0; x--) { if (colCount[x] > h * 0.20) { rightEdge = x; break; } }
        for (int y = h - 1; y >= 0; y--) { if (rowCount[y] > w * 0.20) { bottomEdge = y; break; } }
        sb.AppendLine("bbox_content x=" + minX + " y=" + minY + " w=" + (maxX - minX + 1) + " h=" + (maxY - minY + 1));
        sb.AppendLine("sustained_right_x=" + rightEdge + "  sustained_bottom_y=" + bottomEdge);

        sb.Append("samples:");
        int[] sxp = { 5, 200, 700, 1300, 1900, 2500, 2555 };
        int[] syp = { 5, 200, 700, 1200, 1435 };
        foreach (int y in syp)
        {
            foreach (int x in sxp)
            {
                if (x >= w || y >= h) continue;
                int i = y * stride + x * 4;
                sb.Append(" (" + x + "," + y + ")=" + px[i + 2] + "/" + px[i + 1] + "/" + px[i]);
            }
        }
        sb.AppendLine();

        sb.Append(Components(px, stride, w, h, "felt", 12));
        sb.Append(Components(px, stride, w, h, "bright", 25));
        sb.Append(Components(px, stride, w, h, "lgreen", 15));
        return sb.ToString();
    }

    private static string Components(byte[] px, int stride, int w, int h, string kind, int topN)
    {
        var sb = new StringBuilder();
        bool[] mask = new bool[w * h];
        for (int y = 0; y < h; y++)
        {
            int off = y * stride;
            for (int x = 0; x < w; x++)
            {
                int i = off + x * 4;
                if (Test(kind, px[i], px[i + 1], px[i + 2])) mask[y * w + x] = true;
            }
        }

        var comps = new List<int[]>();
        bool[] seen = new bool[w * h];
        int[] stack = new int[w * h];
        for (int idx = 0; idx < mask.Length; idx++)
        {
            if (!mask[idx] || seen[idx]) continue;
            int sp = 0;
            stack[sp++] = idx;
            seen[idx] = true;
            int cMinX = w, cMaxX = -1, cMinY = h, cMaxY = -1, area = 0;
            long sr = 0, sg = 0, sbb = 0;
            while (sp > 0)
            {
                int cur = stack[--sp];
                int cy = cur / w, cx = cur - cy * w;
                area++;
                if (cx < cMinX) cMinX = cx; if (cx > cMaxX) cMaxX = cx;
                if (cy < cMinY) cMinY = cy; if (cy > cMaxY) cMaxY = cy;
                int ii = cy * stride + cx * 4;
                sbb += px[ii]; sg += px[ii + 1]; sr += px[ii + 2];
                if (cx > 0 && mask[cur - 1] && !seen[cur - 1]) { seen[cur - 1] = true; stack[sp++] = cur - 1; }
                if (cx < w - 1 && mask[cur + 1] && !seen[cur + 1]) { seen[cur + 1] = true; stack[sp++] = cur + 1; }
                if (cy > 0 && mask[cur - w] && !seen[cur - w]) { seen[cur - w] = true; stack[sp++] = cur - w; }
                if (cy < h - 1 && mask[cur + w] && !seen[cur + w]) { seen[cur + w] = true; stack[sp++] = cur + w; }
            }
            comps.Add(new int[] { cMinX, cMinY, cMaxX - cMinX + 1, cMaxY - cMinY + 1, area,
                                  (int)(sr / area), (int)(sg / area), (int)(sbb / area) });
        }

        comps.Sort((a, b) => b[4].CompareTo(a[4]));
        sb.AppendLine("---- mask '" + kind + "': " + comps.Count + " components (top " + topN + ") ----");
        for (int i = 0; i < Math.Min(topN, comps.Count); i++)
        {
            int[] c = comps[i];
            double solidity = (double)c[4] / Math.Max(1, c[2] * c[3]);
            sb.AppendLine(string.Format("  #{0} x={1} y={2} w={3} h={4} area={5} solid={6:F2} rgb={7},{8},{9} cx={10} cy={11}",
                i, c[0], c[1], c[2], c[3], c[4], solidity, c[5], c[6], c[7], c[0] + c[2] / 2, c[1] + c[3] / 2));
        }
        return sb.ToString();
    }
}
'@

Add-Type -TypeDefinition $src -ReferencedAssemblies 'System.Drawing'

$out = 'C:\Users\Neslo\source\repos\PokerNoteManager\tools\shots_analysis.txt'
$sb = New-Object System.Text.StringBuilder
foreach ($p in $Paths)
{
    if (-not (Test-Path $p)) { Write-Host "MISSING: $p"; continue }
    [void]$sb.AppendLine([ShotAnalyzer]::Analyze($p))
}
$sb.ToString() | Out-File -FilePath $out -Encoding UTF8
Write-Host "written: $out"
