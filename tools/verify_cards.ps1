# Which board-card counter works on the real Unibet screenshots at 9-tile, 2-tile and full screen?
# Compares the shipped approach (bright mask + 5x5 MorphClose + contour bbox) with a window scaled
# close on the same mask: the 5x5 kernel bridges the small gap between neighbouring cards at small
# window sizes, so three flop cards collapse into one wide blob and the count becomes 0.
# PowerShell 5.1 -> embedded C# stays C# 5 compatible.
# Run: powershell -ExecutionPolicy Bypass -File tools\verify_cards.ps1

Add-Type -AssemblyName System.Drawing

$src = @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

public static class CardCheck
{
    static byte[] PX;
    static int ST, OX, OY, WW, WH;

    static int B(int x, int y) { return PX[(OY + y) * ST + (OX + x) * 4]; }
    static int G(int x, int y) { return PX[(OY + y) * ST + (OX + x) * 4 + 1]; }
    static int R(int x, int y) { return PX[(OY + y) * ST + (OX + x) * 4 + 2]; }
    static int GrayAt(int x, int y) { int b = B(x, y), g = G(x, y), r = R(x, y); return (b * 29 + g * 150 + r * 77) >> 8; }

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

    static List<int[]> Comps(bool[] m, int bw, int bh, int minPixels)
    {
        List<int[]> res = new List<int[]>();
        bool[] seen = new bool[m.Length];
        int[] stack = new int[m.Length];
        for (int i = 0; i < m.Length; i++)
        {
            if (!m[i] || seen[i]) continue;
            int sp = 0; stack[sp++] = i; seen[i] = true;
            int x0 = 999999, y0 = 999999, x1 = -1, y1 = -1, n = 0;
            while (sp > 0)
            {
                int k = stack[--sp];
                int x = k % bw, y = k / bw;
                if (x < x0) x0 = x;
                if (y < y0) y0 = y;
                if (x > x1) x1 = x;
                if (y > y1) y1 = y;
                n++;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= bw || ny >= bh) continue;
                        int nk = ny * bw + nx;
                        if (!m[nk] || seen[nk]) continue;
                        seen[nk] = true; stack[sp++] = nk;
                    }
            }
            if (n >= minPixels) res.Add(new int[] { x0, y0, x1 - x0 + 1, y1 - y0 + 1, n });
        }
        return res;
    }

    public static string Check(string path, int ox, int oy, int ww, int wh, int fx, int fy, int fw, int fh)
    {
        int sw, sh;
        PX = Load(path, out sw, out sh, out ST);
        OX = ox; OY = oy; WW = ww; WH = wh;
        int bx = fx + (int)(fw * 0.15), by = fy + (int)(fh * 0.19);
        int bw = (int)(fw * 0.70), bh = (int)(fh * 0.27);
        if (bx + bw > ww) bw = ww - bx;
        if (by + bh > wh) bh = wh - by;

        StringBuilder sb = new StringBuilder();
        sb.AppendLine("== " + System.IO.Path.GetFileName(path) + "  window " + ww + "x" + wh +
                      "  band " + bx + "," + by + " " + bw + "x" + bh);

        bool[] m = new bool[bw * bh];
        for (int y = 0; y < bh; y++)
            for (int x = 0; x < bw; x++)
                m[y * bw + x] = GrayAt(bx + x, by + y) > 185;

        int[] counts = new int[5];
        for (int x = 0; x < bw; x++)
        {
            int c = 0;
            for (int y = 0; y < bh; y++) if (m[y * bw + x]) c++;
            if (c >= 4) counts[0]++;
            if (c >= 8) counts[1]++;
            if (c >= bh * 0.15) counts[2]++;
            if (c >= bh * 0.30) counts[3]++;
            if (c >= bh * 0.50) counts[4]++;
        }
        sb.AppendLine("   bright columns: >=4px " + counts[0] + "  >=8px " + counts[1] +
                      "  >=15% " + counts[2] + "  >=30% " + counts[3] + "  >=50% " + counts[4]);

        int[] radii = new int[] { 2, 1, 0 };
        foreach (int radius in radii)
        {
            bool[] closed = radius == 0 ? m : Close(m, bw, bh, radius);
            List<int[]> comps = Comps(closed, bw, bh, 30);
            int cards = 0;
            sb.AppendLine("   -- close " + (radius * 2 + 1) + "x" + (radius * 2 + 1) + ": " + comps.Count + " components");
            foreach (int[] c in comps)
            {
                double aspect = c[2] / (double)Math.Max(1, c[3]);
                bool ok = c[2] >= ww * 0.013 && c[2] <= ww * 0.040 && c[3] >= bh * 0.25 && aspect >= 0.50 && aspect <= 1.15;
                if (ok) cards++;
                if (c[4] > 80 || ok)
                    sb.AppendLine("      " + c[0] + "," + c[1] + " " + c[2] + "x" + c[3] + " aspect=" + aspect.ToString("0.00") +
                                  " area=" + c[4] + "  " + (ok ? "CARD" : "reject"));
            }
            sb.AppendLine("      -> cards = " + cards);
        }
        return sb.ToString();
    }
}
'@

Add-Type -TypeDefinition $src -Language CSharp -ReferencedAssemblies System.Drawing

Write-Host (([CardCheck]::Check('C:\Users\Neslo\Pictures\fullscreen.png', 7, 0, 2050, 1184, 369, 346, 1314, 589)))
Write-Host (([CardCheck]::Check('C:\Users\Neslo\Pictures\2tile.png', 7, 0, 1266, 743, 228, 226, 812, 300)))
Write-Host (([CardCheck]::Check('C:\Users\Neslo\Pictures\9tile.png', 7, 0, 837, 502, 151, 160, 537, 198)))
