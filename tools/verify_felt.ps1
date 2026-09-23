# Verifies the shipped felt detector (HSV InRange(35,40,25)-(95,255,255) + 7x7 close +
# largest blob + size gates >=25% W and >=10% H) against the three real Unibet screenshots.
# Everything felt-relative depends on this, so a miss here silently falls back to the old
# window fractions for the pot, board, hole card and dealer zones.
# PowerShell 5.1 -> embedded C# stays C# 5 compatible.
# Run: powershell -ExecutionPolicy Bypass -File tools\verify_felt.ps1

Add-Type -AssemblyName System.Drawing

$src = @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

public static class FeltCheck
{
    static byte[] PX;
    static int ST, OX, OY, WW, WH;

    static int B(int x, int y) { return PX[(OY + y) * ST + (OX + x) * 4]; }
    static int G(int x, int y) { return PX[(OY + y) * ST + (OX + x) * 4 + 1]; }
    static int R(int x, int y) { return PX[(OY + y) * ST + (OX + x) * 4 + 2]; }

    static byte[] Load(string path, out int w, out int h, out int stride)
    {
        Bitmap bmp = new Bitmap(path);
        try
        {
            w = bmp.Width; h = bmp.Height;
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            stride = bd.Stride;
            byte[] px = new byte[stride * h];
            Marshal.Copy(bd.Scan0, px, 0, px.Length);
            bmp.UnlockBits(bd);
            return px;
        }
        finally { bmp.Dispose(); }
    }

    static void Hsv(int b, int g, int r, out int h, out int s, out int v)
    {
        int mx = Math.Max(b, Math.Max(g, r)), mn = Math.Min(b, Math.Min(g, r));
        v = mx;
        int d = mx - mn;
        s = mx == 0 ? 0 : (int)(d * 255.0 / mx);
        if (d == 0) { h = 0; return; }
        double hh;
        if (mx == r) hh = 60.0 * (((g - b) / (double)d) % 6.0);
        else if (mx == g) hh = 60.0 * (((b - r) / (double)d) + 2.0);
        else hh = 60.0 * (((r - g) / (double)d) + 4.0);
        if (hh < 0) hh += 360.0;
        h = (int)Math.Round(hh / 2.0);
    }

    static bool[] Close(bool[] m, int bw, int bh, int radius)
    {
        bool[] dil = new bool[m.Length];
        for (int y = 0; y < bh; y++)
            for (int x = 0; x < bw; x++)
            {
                if (!m[y * bw + x]) continue;
                for (int dy = -radius; dy <= radius; dy++)
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= bw || ny >= bh) continue;
                        dil[ny * bw + nx] = true;
                    }
            }
        bool[] er = new bool[m.Length];
        for (int y = 0; y < bh; y++)
            for (int x = 0; x < bw; x++)
            {
                bool all = true;
                for (int dy = -radius; dy <= radius && all; dy++)
                    for (int dx = -radius; dx <= radius && all; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= bw || ny >= bh) { all = false; break; }
                        if (!dil[ny * bw + nx]) all = false;
                    }
                er[y * bw + x] = all && m[y * bw + x];
            }
        return er;
    }

    public static string Check(string path, int ox, int oy, int ww, int wh, int truthX, int truthY, int truthW, int truthH)
    {
        int sw, sh;
        PX = Load(path, out sw, out sh, out ST);
        OX = ox; OY = oy; WW = ww; WH = wh;
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("== " + System.IO.Path.GetFileName(path) + "  window " + ww + "x" + wh +
                      "  measured felt " + truthX + "," + truthY + " " + truthW + "x" + truthH);

        bool[] m = new bool[ww * wh];
        int inMask = 0;
        for (int y = 0; y < wh; y++)
            for (int x = 0; x < ww; x++)
            {
                int h, s, v;
                Hsv(B(x, y), G(x, y), R(x, y), out h, out s, out v);
                bool on = h >= 35 && h <= 95 && s >= 40 && v >= 25;
                m[y * ww + x] = on;
                if (on) inMask++;
            }
        sb.AppendLine("   HSV mask hit " + inMask + " px (" + (100.0 * inMask / (ww * wh)).ToString("0.0") + "% of the tile)");

        bool[] closed = Close(m, ww, wh, 3);
        bool[] seen = new bool[closed.Length];
        int[] stack = new int[closed.Length];
        int[] best = null;
        long bestArea = 0;
        for (int i = 0; i < closed.Length; i++)
        {
            if (!closed[i] || seen[i]) continue;
            int sp = 0; stack[sp++] = i; seen[i] = true;
            int x0 = 999999, y0 = 999999, x1 = -1, y1 = -1;
            while (sp > 0)
            {
                int k = stack[--sp];
                int x = k % ww, y = k / ww;
                if (x < x0) x0 = x;
                if (y < y0) y0 = y;
                if (x > x1) x1 = x;
                if (y > y1) y1 = y;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= ww || ny >= wh) continue;
                        int nk = ny * ww + nx;
                        if (!closed[nk] || seen[nk]) continue;
                        seen[nk] = true; stack[sp++] = nk;
                    }
            }
            long area = (long)(x1 - x0 + 1) * (y1 - y0 + 1);
            if (area > bestArea) { bestArea = area; best = new int[] { x0, y0, x1 - x0 + 1, y1 - y0 + 1 }; }
        }
        if (best == null) { sb.AppendLine("   -> no blob found  FAIL"); return sb.ToString(); }

        bool gate = best[2] >= ww * 0.25 && best[3] >= wh * 0.10;
        sb.AppendLine("   largest blob " + best[0] + "," + best[1] + " " + best[2] + "x" + best[3] +
                      "   size gates (>=25% W, >=10% H) = " + (gate ? "PASS" : "FAIL"));
        int tdx = best[0] - truthX, tdy = best[1] - truthY, tdw = best[2] - truthW, tdh = best[3] - truthH;
        bool near = Math.Abs(tdx) < 15 && Math.Abs(tdy) < 15 && Math.Abs(tdw) < 25 && Math.Abs(tdh) < 25;
        sb.AppendLine("   delta vs measured: dx=" + tdx + " dy=" + tdy + " dw=" + tdw + " dh=" + tdh + "   " + (near ? "PASS" : "CHECK"));
        return sb.ToString();
    }
}
'@

Add-Type -TypeDefinition $src -Language CSharp -ReferencedAssemblies System.Drawing

Write-Host (([FeltCheck]::Check('C:\Users\Neslo\Pictures\fullscreen.png', 7, 0, 2050, 1184, 410, 377, 1238, 518)))
Write-Host (([FeltCheck]::Check('C:\Users\Neslo\Pictures\2tile.png', 7, 0, 1266, 743, 257, 246, 760, 279)))
Write-Host (([FeltCheck]::Check('C:\Users\Neslo\Pictures\9tile.png', 7, 0, 837, 502, 172, 174, 503, 184)))
