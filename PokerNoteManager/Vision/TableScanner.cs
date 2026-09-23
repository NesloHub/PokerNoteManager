using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpenCvSharp;
using Tesseract;
using Rect = OpenCvSharp.Rect;

namespace PokerNoteManager.Vision
{
    /// <summary>
    /// Reads the player names off a captured table. Kept free of WPF so it can be tested offline
    /// against saved screenshots (see E:\Build\NotesRoundTripTest).
    /// </summary>
    public static class TableScanner
    {
        /// <summary>Words that are part of the client chrome, never a player name.</summary>
        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "pot", "total", "bb", "sb", "big", "small", "blind", "blinds", "ante", "straddle",
            "fold", "folds", "folded", "call", "calls", "called", "raise", "raises", "raised",
            "check", "checks", "bet", "bets", "all", "in", "allin", "push", "pushes",
            "unibet", "pokersite", "table", "tables", "buy", "add", "chips", "chip", "sit", "sits",
            "out", "sitting", "wait", "waiting", "auto", "muck", "show", "post", "posts", "posted",
            "hand", "hands", "id", "min", "max", "nlhe", "holdem", "hold", "omaha", "plo",
            "flop", "turn", "river", "preflop", "hero", "you", "your", "dealer", "button", "time",
            "bank", "bankroll", "cash", "end", "menu", "settings", "lobby", "cashier", "deposit",
            "withdraw", "the", "and", "for", "to", "act", "new", "player", "join", "leave", "note",
            "notes", "view", "history", "stack", "bounty", "level", "rebuy", "ticket", "freeroll",
            "win", "won", "lost", "balance", "session", "info", "help", "back", "next", "previous",
            "cashier", "tournament", "tourney", "sng", "mtt", "cashgame", "fast", "slow", "speed",
            "autotopup", "topup", "auto-rebuy", "showdown", "summary", "last", "hand", "filter",
            "language", "close", "open", "resume", "pause", "sitout",
            // window chrome and felt messages seen on real screenshots
            "texas", "hold", "em", "cest", "cet", "utc", "gmt", "high", "low", "card", "cards",
            "ace", "king", "queen", "jack", "ten", "nine", "eight", "seven", "six", "five", "four",
            "three", "two", "wins", "split", "name", "poker", "top", "up", "buy", "inn", "level",
            "tournaments", "ring", "game", "games", "play", "paused", "sitting", "away", "back", "any"
        };

        /// <summary>Creates the OCR engine, looking for tessdata next to the exe and in app data.</summary>
        public static TesseractEngine CreateEngine()
        {
            List<string> searched = new();
            Exception? lastError = null;

            foreach (string dir in TessdataCandidates())
            {
                searched.Add(dir);
                try
                {
                    if (!File.Exists(Path.Combine(dir, "eng.traineddata"))) continue;
                    TesseractEngine engine = new(dir, "eng", EngineMode.Default);
                    engine.SetVariable("tessedit_char_blacklist", "|[]{}<>~^`");
                    PvLog.Write($"[OCR] engine ready, tessdata: '{dir}'");
                    return engine;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    PvLog.Error("CreateEngine (" + dir + ")", ex);
                }
            }

            string detail = lastError?.InnerException?.Message ?? lastError?.Message ?? "no eng.traineddata found";
            throw new FileNotFoundException(
                "The OCR engine could not start.\r\n\r\n" +
                $"eng.traineddata must lie next to the program in a 'tessdata' folder, and the " +
                $"Tesseract DLLs in an 'x64' folder.\r\n\r\nLooked in:\r\n   {string.Join("\r\n   ", searched)}\r\n\r\n" +
                $"Last error: {detail}");
        }

        /// <summary>The folders the engine searched (used by the --selftest-ocr switch and the log).</summary>
        public static List<string> DescribeCandidates() => TessdataCandidates().ToList();

        private static IEnumerable<string> TessdataCandidates()
        {
            // A self extracting single file app reports the %TEMP% extraction folder as
            // AppContext.BaseDirectory, so the real exe folder comes first.
            string? exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                string? dir = Path.GetDirectoryName(exe);
                if (!string.IsNullOrEmpty(dir))
                {
                    yield return Path.Combine(dir, "tessdata");
                    yield return Path.Combine(dir, "..", "tessdata");
                }
            }

            yield return Path.Combine(AppContext.BaseDirectory, "tessdata");
            yield return Path.Combine(Environment.CurrentDirectory, "tessdata");
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PokerVisionHUD", "tessdata");
        }

        /// <summary>
        /// Finds the green felt - the scale independent frame of reference for every other zone.
        /// Gate values were measured on real Unibet screenshots at 9-tile, 2-tile and full screen.
        /// </summary>
        public static bool FindFelt(Mat bgr, out Rect felt) => FindFelt(bgr, out felt, out _);

        /// <summary>Same, but also reports how solid the felt is (used for tuning and diagnostics).</summary>
        public static bool FindFelt(Mat bgr, out Rect felt, out double fill)
        {
            felt = default;
            fill = 0;
            try
            {
                using Mat hsv = new();
                Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
                using Mat mask = new();
                Cv2.InRange(hsv, new Scalar(35, 40, 25), new Scalar(95, 255, 255), mask);
                using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(7, 7));
                Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);

                Cv2.FindContours(mask, out Point[][] contours, out HierarchyIndex[] _, RetrievalModes.External,
                    ContourApproximationModes.ApproxSimple);

                Rect best = default;
                long bestArea = 0;
                foreach (Point[] c in contours)
                {
                    Rect r = Cv2.BoundingRect(c);
                    long area = (long)r.Width * r.Height;
                    if (area > bestArea) { bestArea = area; best = r; }
                }

                if (best.Width < bgr.Width * 0.25 || best.Height < bgr.Height * 0.10) return false;

                // A real felt is one solid green area (cards, chips and the logo sit on top of it).
                // Scattered green pixels - text in an editor or a chat window - also produce a big
                // bounding box, so the fill ratio and the table like shape decide.
                using (Mat bestMask = new(mask, best))
                    fill = Cv2.CountNonZero(bestMask) / (double)(best.Width * best.Height);
                double aspect = best.Width / (double)Math.Max(1, best.Height);

                if (fill < 0.35 || aspect < 0.8 || aspect > 4.0)
                {
                    PvLog.Throttled("FindFelt", $"   felt candidate rejected: fill={fill:0.00} aspect={aspect:0.00} {best}", 60);
                    return false;
                }

                felt = best;
                return true;
            }
            catch (Exception ex)
            {
                PvLog.Error("TableScanner.FindFelt", ex);
                return false;
            }
        }

        /// <summary>Tesseract is not thread safe: every call goes through this lock.</summary>
        public static readonly object OcrLock = new();

        private sealed class Word
        {
            public string Text = "";
            public float Conf;
            public Rect Box;
        }

        /// <summary>
        /// Reads every player name plate on one captured table. One OCR pass over the whole window:
        /// words are then filtered by the felt relative zones measured on real screenshots, so the
        /// pot, the board strip and the table logo can never be mistaken for a player.
        /// When the table has no green felt (some client themes are grey/white), a window relative
        /// zone plus the "light text on a dark plate" test is used instead.
        /// </summary>
        public static List<SeatBox> DetectSeats(Mat bgr, Rect? felt, TesseractEngine engine, string tableTitle)
        {
            List<SeatBox> seats = new();
            try
            {
                double scale = Math.Clamp(1600.0 / bgr.Width, 1.5, 3.0);
                using Mat big = new();
                Cv2.Resize(bgr, big, new Size(0, 0), scale, scale, InterpolationFlags.Cubic);
                byte[] png;
                Cv2.ImEncode(".png", big, out png);

                List<Word> words = new();
                lock (OcrLock)
                {
                    using Pix pix = Pix.LoadFromMemory(png);
                    // PageSegMode.SingleBlock is deliberate: the layout analysis modes (Auto, SparseText)
                    // crash the native Tesseract build used here, and we do our own layout work anyway
                    // (the felt relative zones decide which words belong to a seat).
                    using Page page = engine.Process(pix, PageSegMode.SingleBlock);
                    using ResultIterator it = page.GetIterator();
                    it.Begin();
                    do
                    {
                        string text = (it.GetText(PageIteratorLevel.Word) ?? "").Trim();
                        if (text.Length == 0) continue;
                        if (!it.TryGetBoundingBox(PageIteratorLevel.Word, out Tesseract.Rect tr)) continue;
                        float conf = it.GetConfidence(PageIteratorLevel.Word);
                        double inv = 1.0 / scale;
                        Rect box = new((int)(tr.X1 * inv), (int)(tr.Y1 * inv),
                                       Math.Max(1, (int)((tr.X2 - tr.X1) * inv)), Math.Max(1, (int)((tr.Y2 - tr.Y1) * inv)));
                        if (!LooksLikeName(text)) continue;
                        if (box.Height < bgr.Height * 0.012 || box.Width > bgr.Width * 0.30) continue;
                        bool inSeat = felt.HasValue ? IsSeatRing(box, felt.Value, bgr) : IsWindowRing(box, bgr);
                        if (!inSeat) continue;
                        if (!IsOnDarkPlate(bgr, box)) continue;      // name plates are light text on a dark plate
                        words.Add(new Word { Text = Clean(text), Conf = conf, Box = box });
                    }
                    while (it.Next(PageIteratorLevel.Word));
                }

                foreach (SeatBox seat in MergeWords(words, bgr.Width, tableTitle))
                    seats.Add(seat);

                return seats;
            }
            catch (Exception ex)
            {
                PvLog.Error("TableScanner.DetectSeats", ex);
                return seats;
            }
        }

        /// <summary>
        /// Window titles that are worth reading even without a green felt. The hints are deliberately
        /// about poker *tables* and client brands - a bare "poker" would also match browser tabs and
        /// this program's own windows.
        /// </summary>
        public static bool LooksLikePokerTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return false;
            foreach (string hint in new[] { "hold'em", "holdem", "hold em", "omaha", "texas", "shortdeck",
                                            "short deck", "tournament", "freeroll", "sit & go", "sit and go",
                                            "unibet", "winamax", "ggpoker", "gg poker", "pokerstars",
                                            "partypoker", "ipoker", "888poker", "betfair", "paddypower",
                                            "sky poker", "ggnetwork" })
                if (title.Contains(hint, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Fallback zone for tables without a green felt: the middle of the window, without the window
        /// chrome at the top and the button/hand-id bar at the bottom.
        /// </summary>
        private static bool IsWindowRing(Rect box, Mat frame)
        {
            double cy = box.Y + box.Height / 2.0;
            if (box.Height > frame.Height * 0.06) return false;
            if (cy < frame.Height * 0.15 || cy > frame.Height * 0.86) return false;
            return true;
        }

        /// <summary>
        /// True when a word sits in the ring around the felt where the name plates live - and not in
        /// the pot band, the board strip or the middle of the felt (table logo).
        /// </summary>
        private static bool IsSeatRing(Rect box, Rect felt, Mat frame)
        {
            double cx = box.X + box.Width / 2.0;
            double cy = box.Y + box.Height / 2.0;
            double fw = felt.Width, fh = felt.Height;

            if (box.Height > frame.Height * 0.06) return false;                 // too tall for a name plate
            // The seat ring is tight on purpose: window chrome (title bar, tab bar, table menu) lives
            // further away from the felt, and it would otherwise be read as player names.
            if (cx < felt.X - fw * 0.15 || cx > felt.Right + fw * 0.15) return false;
            if (cy < felt.Y - fh * 0.45 || cy > felt.Bottom + fh * 0.60) return false;
            if (cy < frame.Height * 0.035 || cy > frame.Height * 0.97) return false;

            bool InBand(double bx, double by, double bw, double bh) =>
                cx >= felt.X + fw * bx && cx <= felt.X + fw * (bx + bw) &&
                cy >= felt.Y + fh * by && cy <= felt.Y + fh * (by + bh);

            if (InBand(0.10, 0.00, 0.80, 0.21)) return false;    // total pot
            if (InBand(0.15, 0.19, 0.70, 0.27)) return false;    // community cards
            if (InBand(0.25, 0.45, 0.50, 0.30)) return false;    // felt centre / logo
            return true;
        }

        private static string Clean(string text) =>
            new string(text.Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '_' || c == '-' || c == '.').ToArray()).Trim();

        /// <summary>
        /// Name plates are light text on a dark plate. Text in the window chrome (title bar, tab bar)
        /// sits on a light background, so the ring around the box decides which one it is.
        /// </summary>
        private static bool IsOnDarkPlate(Mat bgr, Rect box)
        {
            try
            {
                int padX = Math.Max(4, box.Width / 3);
                int padY = Math.Max(3, box.Height / 2);
                int x0 = Math.Max(0, box.X - padX), y0 = Math.Max(0, box.Y - padY);
                int x1 = Math.Min(bgr.Width, box.Right + padX), y1 = Math.Min(bgr.Height, box.Bottom + padY);
                if (x1 - x0 < 6 || y1 - y0 < 4) return true;

                using Mat roi = new(bgr, new Rect(x0, y0, x1 - x0, y1 - y0));
                using Mat gray = new();
                Cv2.CvtColor(roi, gray, ColorConversionCodes.BGR2GRAY);

                // mean of the whole patch and of the ring (patch minus the text box itself)
                Scalar meanAll = Cv2.Mean(gray);
                using Mat ring = new(gray, new Rect(0, 0, gray.Width, gray.Height));
                Cv2.Rectangle(ring, new Rect(box.X - x0, box.Y - y0, box.Width, box.Height), Scalar.All(0), -1);
                Scalar meanRing = Cv2.Mean(ring);
                int textPixels = Cv2.CountNonZero(gray);
                if (textPixels == 0) return false;

                // dark plate: the ring must be clearly darker than the glyphs
                return meanRing.Val0 < 140 && meanRing.Val0 < meanAll.Val0 + 40;
            }
            catch (Exception ex)
            {
                PvLog.Throttled("IsOnDarkPlate", $"!! plate test: {ex.Message}", 120);
                return true;
            }
        }

        private static bool LooksLikeName(string raw)
        {
            string t = Clean(raw);
            if (t.Length < 2 || t.Length > 20) return false;
            if (StopWords.Contains(t)) return false;

            int letters = t.Count(char.IsLetter);
            int digits = t.Count(char.IsDigit);
            if (letters == 0) return false;
            if (digits > letters + 2) return false;                  // "1.00", "12,345" style amounts
            if (t.Contains('.') && digits >= 1) return false;        // stack/bet values
            if (t.Length <= 2 && !t.Any(c => c > 127) && letters < 2) return false;
            return true;
        }

        /// <summary>Joins words that sit on the same line: OCR often splits a name in two.</summary>
        private static List<SeatBox> MergeWords(List<Word> words, int frameWidth, string tableTitle)
        {
            List<SeatBox> result = new();
            if (words.Count == 0) return result;

            int maxGap = Math.Max(8, (int)(frameWidth * 0.012));
            List<Word> ordered = words.OrderBy(w => w.Box.Y).ThenBy(w => w.Box.X).ToList();
            bool[] used = new bool[ordered.Count];

            for (int i = 0; i < ordered.Count; i++)
            {
                if (used[i]) continue;
                used[i] = true;
                List<Word> group = new() { ordered[i] };

                bool added = true;
                while (added)
                {
                    added = false;
                    for (int j = 0; j < ordered.Count && !added; j++)
                    {
                        if (used[j]) continue;
                        Word other = ordered[j];
                        foreach (Word member in group)
                        {
                            int vOverlap = Math.Min(member.Box.Bottom, other.Box.Bottom) - Math.Max(member.Box.Y, other.Box.Y);
                            if (vOverlap < Math.Min(member.Box.Height, other.Box.Height) * 0.5) continue;
                            int gap = Math.Max(member.Box.X, other.Box.X) - Math.Min(member.Box.Right, other.Box.Right);
                            if (gap > maxGap) continue;
                            group.Add(other);
                            used[j] = true;
                            added = true;
                            break;
                        }
                    }
                }

                group = group.OrderBy(w => w.Box.X).ToList();
                Rect box = group[0].Box;
                foreach (Word w in group.Skip(1)) box = box.Union(w.Box);

                result.Add(new SeatBox
                {
                    TableTitle = tableTitle,
                    OcrText = string.Join(" ", group.Select(w => w.Text)),
                    Confidence = group.Min(w => w.Conf),
                    ScreenRect = new Rect(Math.Max(0, box.X - 5), Math.Max(0, box.Y - 4), box.Width + 10, box.Height + 8)
                });
            }

            return result;
        }

        /// <summary>
        /// A short hint when the OCR engine cannot start, so a missing tessdata folder is not just a
        /// cryptic Tesseract error in the status line. Returns an empty string for unrelated errors.
        /// </summary>
        public static string DescribeEngineProblem(Exception ex)
        {
            System.Text.StringBuilder text = new();
            Exception? current = ex;
            int depth = 0;
            while (current != null && depth++ < 5)
            {
                text.Append(current.Message).Append(' ');
                current = current.InnerException;
            }
            string all = text.ToString().ToLowerInvariant();

            bool ocrProblem = all.Contains("tesseract") || all.Contains("traineddata") || all.Contains("tessdata") ||
                              all.Contains("leptonica") || all.Contains("initialis") || all.Contains("path1") ||
                              all.Contains("failed to");
            if (!ocrProblem) return "";

            string dir = System.IO.Path.Combine(AppContext.BaseDirectory, "tessdata");
            return $"  ->  The OCR files are needed ({dir} with eng.traineddata). " +
                   "Run PokerVisionHUD.exe --selftest-ocr to see exactly which path is missing.";
        }

        /// <summary>Reads one line of text (used by "Snip Player"): upscaled, SingleLine OCR.</summary>
        public static string ReadSingleLine(Mat bgr, TesseractEngine engine)
        {
            try
            {
                double scale = Math.Clamp(90.0 / Math.Max(1, bgr.Height), 1.5, 6.0);
                using Mat big = new();
                Cv2.Resize(bgr, big, new Size(0, 0), scale, scale, InterpolationFlags.Cubic);
                byte[] png;
                Cv2.ImEncode(".png", big, out png);

                lock (OcrLock)
                {
                    using Pix pix = Pix.LoadFromMemory(png);
                    using Page page = engine.Process(pix, PageSegMode.SingleLine);
                    return (page.GetText() ?? "").Replace("\n", " ").Trim();
                }
            }
            catch (Exception ex)
            {
                PvLog.Error("TableScanner.ReadSingleLine", ex);
                return "";
            }
        }

        // ================= name matching =================

        /// <summary>
        /// OCR visual homoglyph normalisation: 1/l/i/I/|, 0/o, 5/s, 8/b, 3/e, vv→w, rn→m and no
        /// spaces - so "ug7z" is the same player as "U87" and "ninja tin" matches "ninjatin".
        /// </summary>
        public static string NormalizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            string lower = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            return lower.Replace("1", "l").Replace("i", "l").Replace("|", "l")
                        .Replace("0", "o")
                        .Replace("5", "s").Replace("$", "s")
                        .Replace("8", "b")
                        .Replace("3", "e")
                        .Replace("vv", "w")
                        .Replace("rn", "m");
        }

        /// <summary>Best matching database key for an OCR reading ("" when nothing is close enough).</summary>
        public static (string Key, double Distance) MatchPlayer(string ocrText, Dictionary<string, string> normalizedKeys)
        {
            string norm = NormalizeName(ocrText);
            if (norm.Length < 3) return ("", double.MaxValue);

            string bestKey = "";
            double best = double.MaxValue;
            foreach (KeyValuePair<string, string> kv in normalizedKeys)
            {
                if (kv.Value.Length < 3) continue;
                if (kv.Value == norm) return (kv.Key, 0);

                int allowed = Math.Max(1, kv.Value.Length / 6);
                int d = Levenshtein(norm, kv.Value, allowed + 1);
                if (d > allowed) continue;
                if (d < best) { best = d; bestKey = kv.Key; }
            }
            return (bestKey, best);
        }

        private static int Levenshtein(string a, string b, int limit)
        {
            if (Math.Abs(a.Length - b.Length) > limit) return limit + 1;
            int[] prev = new int[b.Length + 1];
            int[] cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                int rowMin = cur[0];
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                    if (cur[j] < rowMin) rowMin = cur[j];
                }
                if (rowMin > limit) return limit + 1;
                (prev, cur) = (cur, prev);
            }
            return prev[b.Length];
        }
    }
}
