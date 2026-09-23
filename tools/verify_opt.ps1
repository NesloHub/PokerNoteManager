# Data driven check of two detector fixes against the three real Unibet screenshots:
#   1) board card counting by column projection (the old 5x5 MorphClose merged neighbouring
#      cards at 9-tile / 2-tile, so the flop was never seen and no hand was ever committed)
#   2) the per seat stack search box: does it reach the green stack line in every layout, and
#      does it wrongly swallow the green pot text?
# PowerShell 5.1 -> the embedded C# must stay C# 5 compatible (no local functions, no "int[]?", no "=>" bodies).
# Run: powershell -ExecutionPolicy Bypass -File tools\verify_opt.ps1

Add-Type -AssemblyName System.Drawing

$src = @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

public static class OptCheck
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

    // kind 0 = bright white text, 1 = green text
    static bool Mask(int kind, int x, int y)
    {
        int b = B(x, y), g = G(x, y), r = R(x, y);
        if (kind == 1) return g > 150 && g - b > 30 && g - r > 10;
        return r > 215 && g > 215 && b > 215;
    }

    static List<int[]> Blobs(int kind, int minPixels)
    {
        bool[] m = new bool[WW * WH];
        for (int y = 0; y < WH; y++)
            for (int x = 0; x < WW; x++)
                m[y * WW + x] = Mask(kind, x, y);

        List<int[]> res = new List<int[]>();
        bool[] seen = new bool[WW * WH];
        int[] stack = new int[WW * WH];
        for (int i = 0; i < m.Length; i++)
        {
            if (!m[i] || seen[i]) continue;
            int sp = 0; stack[sp++] = i; seen[i] = true;
            int x0 = 999999, y0 = 999999, x1 = -1, y1 = -1, n = 0;
            while (sp > 0)
            {
                int k = stack[--sp];
                int x = k % WW, y = k / WW;
                if (x < x0) x0 = x;
                if (y < y0) y0 = y;
                if (x > x1) x1 = x;
                if (y > y1) y1 = y;
                n++;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= WW || ny >= WH) continue;
                        int nk = ny * WW + nx;
                        if (!m[nk] || seen[nk]) continue;
                        seen[nk] = true; stack[sp++] = nk;
                    }
            }
            if (n >= minPixels) res.Add(new int[] { x0, y0, x1 - x0 + 1, y1 - y0 + 1, n });
        }
        return res;
    }

    static bool Inside(int[] box, int cx, int cy)
    {
        return cx >= box[0] && cx <= box[0] + box[2] && cy >= box[1] && cy <= box[1] + box[3];
    }

    public static string Check(string path, int ox, int oy, int ww, int wh, int fx, int fy, int fw, int fh)
    {
        int sw, sh;
        PX = Load(path, out sw, out sh, out ST);
        OX = ox; OY = oy; WW = ww; WH = wh;
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("== " + System.IO.Path.GetFileName(path) + "  window " + ww + "x" + wh + "  felt " + fx + "," + fy + " " + fw + "x" + fh);

        // ---------- 1) board cards by column projection ----------
        int bx = fx + (int)(fw * 0.15), by = fy + (int)(fh * 0.10);
        int bw = (int)(fw * 0.70), bh = (int)(fh * 0.34);
        if (bx + bw > ww) bw = ww - bx;
        if (by + bh > wh) bh = wh - by;
        int colThreshold = (int)(bh * 0.30);
        bool[] col = new bool[bw];
        for (int x = 0; x < bw; x++)
        {
            int c = 0;
            for (int y = 0; y < bh; y++) if (GrayAt(bx + x, by + y) > 185) c++;
            col[x] = c >= colThreshold;
        }
        double minCardW = ww * 0.013, maxCardW = ww * 0.035;
        int maxGap = Math.Max(2, (int)(ww * 0.0015));
        sb.AppendLine("   board band " + bx + "," + by + " " + bw + "x" + bh + "  colThreshold=" + colThreshold + "  maxGap=" + maxGap);
        int cards = 0, runStart = -1, gap = 0;
        for (int x = 0; x <= bw; x++)
        {
            bool on = x < bw && col[x];
            if (on)
            {
                if (runStart < 0) runStart = x;
                gap = 0;
                continue;
            }
            if (runStart < 0) continue;
            gap++;
            if (gap > maxGap || x == bw)
            {
                int end = x - gap;
                int rw = end - runStart + 1, ry0 = 999999, ry1 = -1;
                for (int gx = runStart; gx <= end; gx++)
                    for (int gy = 0; gy < bh; gy++)
                        if (GrayAt(bx + gx, by + gy) > 185)
                        {
                            if (gy < ry0) ry0 = gy;
                            if (gy > ry1) ry1 = gy;
                        }
                int rh = ry1 - ry0 + 1;
                bool ok = rw >= minCardW && rw <= maxCardW * 1.6 && rh >= bh * 0.25;
                sb.AppendLine("   group x=" + runStart + " w=" + rw + " h=" + rh + "  " + (ok ? "CARD" : "reject"));
                if (ok) cards++;
                runStart = -1;
            }
        }
        sb.AppendLine("   -> board cards = " + cards);

        // ---------- 2) stack search box reach ----------
        List<int[]> green = Blobs(1, 4);
        List<int[]> white = Blobs(0, 12);
        int[] potBand = new int[] { fx + (int)(fw * 0.10), fy, (int)(fw * 0.80), (int)(fh * 0.18) };

        sb.AppendLine("   green text blobs: " + green.Count + "  white blobs: " + white.Count);
        int hitOld = 0, hitNew = 0, inPot = 0, total = 0, noPlate = 0;
        foreach (int[] g in green)
        {
            double hFrac = g[3] / (double)wh;
            if (hFrac < 0.008 || hFrac > 0.070) continue;
            total++;
            int gcx = g[0] + g[2] / 2, gcy = g[1] + g[3] / 2;
            int[] plate = null; long bestD = long.MaxValue;
            foreach (int[] w in white)
            {
                if (w[3] > wh * 0.09 || w[2] > ww * 0.25) continue;
                int wcx = w[0] + w[2] / 2, wcy = w[1] + w[3] / 2;
                if (wcy > gcy) continue;
                if (Math.Abs(wcx - gcx) > ww * 0.10) continue;
                long d = ((long)(gcy - wcy)) * 1000 + Math.Abs(wcx - gcx);
                if (d < bestD) { bestD = d; plate = w; }
            }
            if (plate == null)
            {
                noPlate++;
                sb.AppendLine("      blob " + g[0] + "," + g[1] + " " + g[2] + "x" + g[3] + " -> no nameplate found above");
                continue;
            }
            double sx = plate[0] + plate[2] / 2.0, sy = plate[1] + plate[3] / 2.0;
            int[] oldBox = new int[] { (int)(sx - ww * 0.065), (int)(sy - wh * 0.04), (int)(ww * 0.13), (int)(wh * 0.15) };
            int[] newBox = new int[] { (int)(sx - ww * 0.10), (int)(sy - wh * 0.05), (int)(ww * 0.20), (int)(wh * 0.25) };
            bool o = Inside(oldBox, gcx, gcy), n = Inside(newBox, gcx, gcy), p = Inside(potBand, gcx, gcy);
            if (o) hitOld++;
            if (n) hitNew++;
            if (p) inPot++;
            sb.AppendLine("      blob " + g[0] + "," + g[1] + " " + g[2] + "x" + g[3] +
                          "  plate " + (int)sx + "," + (int)sy +
                          "  oldBox=" + (o ? "in" : "OUT") + "  newBox=" + (n ? "in" : "OUT") +
                          "  inPotBand=" + (p ? "yes" : "no"));
        }
        sb.AppendLine("   -> stack lines " + total + " (no plate: " + noPlate + ")  old box reached " + hitOld +
                      "  new box reached " + hitNew + "  of which in pot band " + inPot);
        return sb.ToString();
    }
}
'@

Add-Type -TypeDefinition $src -Language CSharp -ReferencedAssemblies System.Drawing

Write-Host (([OptCheck]::Check('C:\Users\Neslo\Pictures\fullscreen.png', 7, 0, 2050, 1184, 410, 377, 1238, 518)))
Write-Host (([OptCheck]::Check('C:\Users\Neslo\Pictures\2tile.png', 7, 0, 1266, 743, 257, 246, 760, 279)))
Write-Host (([OptCheck]::Check('C:\Users\Neslo\Pictures\9tile.png', 7, 0, 837, 502, 172, 174, 503, 184)))
