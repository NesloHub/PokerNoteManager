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
        /// Reads every player name plate on one captured table.
        ///
        /// Three steps are combined, which is why a scan takes a little longer than before (that is fine,
        /// accuracy matters more):
        ///   1. the word pass over the whole window (proven on real tables) finds the text lines, and the
        ///      felt relative zones keep the pot, the board, the logo, chips and avatars out;
        ///   2. every word that was found is read *again* on its own and upscaled - that reading is usually
        ///      better than the one from the whole window, and it wins whenever it is not worse;
        ///   3. name plates the word pass did not see at all (a name it never recognised as a word) are
        ///      added afterwards.
        /// </summary>
        public static List<SeatBox> DetectSeats(Mat bgr, Rect? felt, TesseractEngine engine, string tableTitle)
        {
            // 1) the word pass: it finds the text lines, even when a single word is read badly
            List<SeatBox> seats = ReadWordsInWindow(bgr, felt, engine, tableTitle);

            // 2) the plate pass: it knows where the name plates are, reads each one on its own and fills in
            //    the plates the word pass did not see. Where both passes describe the same plate, the better
            //    reading wins and the wider (plate) rectangle is kept - the word box is often only part of
            //    the name, which used to make a name come out as "teel" instead of "JimSteele".
            foreach (SeatBox plate in ReadNamePlates(bgr, felt, engine, tableTitle))
            {
                SeatBox? twin = seats.FirstOrDefault(s => Overlaps(s.ScreenRect, plate.ScreenRect));
                if (twin == null)
                {
                    seats.Add(plate);
                    continue;
                }

                // The plate pass reads the name line itself, so a reading that is a bit better wins too
                // (a merged word pass box can be read as "beeb" where the plate gives "beeboop").
                if (plate.Confidence > twin.Confidence + 3)
                {
                    PvLog.Throttled("PlateWins:" + plate.ScreenRect.X + "x" + plate.ScreenRect.Y,
                        $"[OCR] '{twin.OcrText}' (conf {twin.Confidence:0}) -> '{plate.OcrText}' " +
                        $"(conf {plate.Confidence:0}) from the name plate", 60);
                    twin.OcrText = plate.OcrText;
                    twin.Confidence = plate.Confidence;
                }

                if (plate.ScreenRect.Width > twin.ScreenRect.Width) twin.ScreenRect = plate.ScreenRect;
            }

            return seats;
        }

        /// <summary>
        /// True when two boxes cover (roughly) the same piece of the table: the word pass and the plate pass
        /// describe the same name plate with slightly different rectangles, and those must not become two
        /// boxes for one player.
        /// </summary>
        private static bool Overlaps(Rect a, Rect b)
        {
            int x0 = Math.Max(a.X, b.X), y0 = Math.Max(a.Y, b.Y);
            int x1 = Math.Min(a.Right, b.Right), y1 = Math.Min(a.Bottom, b.Bottom);
            if (x1 <= x0 || y1 <= y0) return false;

            double inter = (x1 - x0) * (double)(y1 - y0);
            double union = a.Width * (double)a.Height + b.Width * (double)b.Height - inter;
            if (union > 0 && inter / union > 0.2) return true;

            return Contains(a, b) || Contains(b, a);
        }

        /// <summary>True when the centre of <paramref name="inner"/> lies inside <paramref name="outer"/>.</summary>
        private static bool Contains(Rect outer, Rect inner)
        {
            double cx = inner.X + inner.Width / 2.0, cy = inner.Y + inner.Height / 2.0;
            return cx >= outer.X && cx <= outer.Right && cy >= outer.Y && cy <= outer.Bottom;
        }

        /// <summary>The accurate pass: every candidate name plate is read on its own.</summary>
        private static List<SeatBox> ReadNamePlates(Mat bgr, Rect? felt, TesseractEngine engine, string tableTitle)
        {
            List<SeatBox> seats = new();
            try
            {
                foreach (Rect plate in FindNamePlates(bgr, felt))
                {
                    string text = TryReadPlate(bgr, plate, engine, out float confidence);
                    if (text.Length == 0) continue;

                    seats.Add(new SeatBox
                    {
                        TableTitle = tableTitle,
                        OcrText = text,
                        Confidence = confidence,
                        ScreenRect = new Rect(Math.Max(0, plate.X - 4), Math.Max(0, plate.Y - 3),
                                              plate.Width + 8, plate.Height + 6)
                    });
                }
            }
            catch (Exception ex)
            {
                PvLog.Error("TableScanner.ReadNamePlates", ex);
            }
            return seats;
        }

        /// <summary>
        /// The fallback pass: one OCR over the whole window, words filtered by the felt relative zones.
        /// Used only when no name plate could be found at all.
        /// </summary>
        private static List<SeatBox> ReadWordsInWindow(Mat bgr, Rect? felt, TesseractEngine engine, string tableTitle)
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
                        if (box.Height < bgr.Height * 0.012 || box.Height > bgr.Height * 0.06) continue;
                        if (box.Width > bgr.Width * 0.30) continue;
                        bool inSeat = felt.HasValue ? IsSeatRing(box, felt.Value, bgr) : IsWindowRing(box, bgr);
                        if (!inSeat) continue;
                        if (!IsOnDarkPlate(bgr, box)) continue;
                        words.Add(new Word { Text = Clean(text), Conf = conf, Box = box });
                    }
                    while (it.Next(PageIteratorLevel.Word));
                }

                foreach (SeatBox seat in MergeWords(words, bgr.Width, bgr.Height, tableTitle))
                    seats.Add(seat);
            }
            catch (Exception ex)
            {
                PvLog.Error("TableScanner.ReadWordsInWindow", ex);
            }
            return seats;
        }

        /// <summary>
        /// The name plates on a table: light text on a dark plate, inside the seat ring (and never in the pot
        /// band, the board strip or the middle of the felt). The text is thresholded and joined per line, so
        /// every candidate is a *line of text* - chips, avatars and logos do not survive the plate test.
        /// </summary>
        private static List<Rect> FindNamePlates(Mat bgr, Rect? felt)
        {
            List<Rect> found = new();
            try
            {
                using Mat gray = new();
                Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
                using Mat bright = new();
                Cv2.InRange(gray, Scalar.All(150), Scalar.All(255), bright);      // light name plate text

                int join = Math.Max(7, bgr.Width / 260);                          // scales with the window
                using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(join, 3));
                using Mat joined = new();
                Cv2.MorphologyEx(bright, joined, MorphTypes.Close, kernel);

                Cv2.FindContours(joined, out Point[][] contours, out _,
                    RetrievalModes.External, ContourApproximationModes.ApproxSimple);

                foreach (Point[] c in contours)
                {
                    Rect r = Cv2.BoundingRect(c);
                    if (r.Width < Math.Max(16, bgr.Width / 40)) continue;          // too short for a name
                    if (r.Width > bgr.Width * 0.35) continue;
                    if (r.Height < Math.Max(8, bgr.Height * 0.012) || r.Height > bgr.Height * 0.06) continue;

                    bool inSeat = felt.HasValue ? IsSeatRing(r, felt.Value, bgr) : IsWindowRing(r, bgr);
                    if (!inSeat) continue;
                    if (!IsOnDarkPlate(bgr, r))
                    {
                        PvLog.Throttled("NoPlate:" + r.X + "x" + r.Y,
                            $"[OCR] text at {r} skipped: no dark flat name plate", 120);
                        continue;
                    }
                    found.Add(r);
                }
            }
            catch (Exception ex)
            {
                PvLog.Error("TableScanner.FindNamePlates", ex);
            }

            return found.OrderByDescending(r => r.Width).Take(12).ToList();        // widest first: seat names
        }

        /// <summary>
        /// Reads the best name-like text out of a set of crops of the same image: every crop is OCR'd and
        /// the reading with the highest confidence wins. Which crop is best differs from window to window
        /// (a tight crop around the glyphs is usually best, but a plate with an odd font can be read better
        /// in one piece), so both are tried instead of guessing.
        /// </summary>
        private static string BestRead(Mat image, IReadOnlyList<Rect> crops, TesseractEngine engine, out float confidence,
                                       float minConfidence = 0, float preferConfidence = 0, Action<Rect>? winner = null)
        {
            string best = "";
            float bestConf = 0;
            confidence = 0;

            foreach (Rect crop in crops)
            {
                if (crop.Width < 8 || crop.Height < 6 || crop.Right > image.Width || crop.Bottom > image.Height) continue;

                using Mat view = new(image, crop);

                // A name plate is light text on a dark plate - the opposite of what the OCR files were
                // trained on - so every crop is also read as its negative. That single extra attempt is
                // what reads "Sistahumlan" (the plain reading of its plate is "» §i§—t§i1_u imlan", the
                // negative one is the name with a confidence in the nineties).
                List<Mat> variants = Variants(view);
                try
                {
                    // The upscale factors: on the plates of real tables a moderate one (the glyphs end up
                    // around 45 px tall) reads best, while a very small plate needs the large factor.
                    double big = Math.Clamp(150.0 / Math.Max(1, crop.Height), 2.0, 8.0);
                    double[] scales = big > 3.2 ? new[] { 3.0, big } : new[] { big };
                    float enough = preferConfidence > 0 ? preferConfidence : 60f;

                    void Attempt(Mat variant, double scale)
                    {
                        string text = ReadSingleLine(variant, engine, out float conf, scale);
                        if (!LooksLikePlayerName(text) || conf < minConfidence) return;
                        if (conf > bestConf)
                        {
                            best = text;
                            bestConf = conf;
                            winner?.Invoke(crop);      // the crop that won: the caller can box exactly that
                        }
                    }

                    // 1) the plate as it is
                    foreach (double scale in scales)
                    {
                        Attempt(variants[0], scale);
                        if (bestConf >= enough) break;
                    }

                    // 2) only when that stayed weak: the negative. A name plate is light text on a dark
                    //    plate, which is the polarity the OCR files were *not* trained on - and reading the
                    //    negative is what turns "» §i§—t§i1_u imlan" into "Sistahumlan". No extra OCR pass
                    //    is paid for the plates that already read well.
                    if (bestConf < enough && variants.Count > 1)
                    {
                        foreach (double scale in scales)
                        {
                            Attempt(variants[1], scale);
                            if (bestConf >= enough) break;
                        }
                    }
                }
                finally
                {
                    foreach (Mat extra in variants.Skip(1)) extra.Dispose();
                }

                if (preferConfidence > 0 && bestConf >= preferConfidence) break;
            }

            confidence = bestConf;
            return best;
        }

        /// <summary>
        /// The images one crop is read as: the crop itself and its negative. Both have to be disposed by
        /// the caller - the first entry is the given matrix, which the caller owns.
        /// </summary>
        private static List<Mat> Variants(Mat bgr)
        {
            List<Mat> variants = new() { bgr };
            try
            {
                using Mat gray = new();
                if (bgr.Channels() == 3) Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
                else bgr.CopyTo(gray);

                using Mat negative = new();
                Cv2.BitwiseNot(gray, negative);

                Mat asBgr = new();
                Cv2.CvtColor(negative, asBgr, ColorConversionCodes.GRAY2BGR);
                variants.Add(asBgr);
            }
            catch (Exception ex)
            {
                PvLog.Error("TableScanner.Variants", ex);
            }
            return variants;
        }

        /// <summary>
        /// Reads one name plate and returns the text ("" when nothing name-like could be read). Three crops are
        /// tried: tight around the glyphs (the row below the name - the stack size - is what turns the reading
        /// into nonsense, so the tight crop is the most important one), a little wider, and a bit wider still.
        /// </summary>
        private static string TryReadPlate(Mat bgr, Rect plate, TesseractEngine engine, out float confidence)
        {
            List<Rect> crops = new()
            {
                Pad(plate, bgr.Size(), 2, 2),
                Pad(plate, bgr.Size(), 5, 4),
                Pad(plate, bgr.Size(), 9, 7)
            };

            string best = BestRead(bgr, crops, engine, out confidence, 0, 65);
            if (best.Length > 0)
                PvLog.Throttled("Plate:" + plate.X + "x" + plate.Y,
                    $"[OCR] plate {plate} -> '{best}' (conf {confidence:0})", 120);
            return best;
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
        /// Name plates are light text on a dark, flat plate. Text in the window chrome (title bar,
        /// tab bar) sits on a light background and fails here, and so does anything that is not a plate
        /// at all: an avatar photo with glasses that the OCR reads as letters ("C0ol") has a busy,
        /// colourful surround and its glyphs do not sit on one plate colour, so it is rejected.
        /// </summary>
        private static bool IsOnDarkPlate(Mat bgr, Rect box)
        {
            try
            {
                // The ring is kept tight around the glyphs so it stays on the plate itself.
                int padX = Math.Max(3, box.Width / 4);
                int padY = Math.Max(2, box.Height / 3);
                int x0 = Math.Max(0, box.X - padX), y0 = Math.Max(0, box.Y - padY);
                int x1 = Math.Min(bgr.Width, box.Right + padX), y1 = Math.Min(bgr.Height, box.Bottom + padY);
                if (x1 - x0 < 6 || y1 - y0 < 4) return true;

                using Mat roi = new(bgr, new Rect(x0, y0, x1 - x0, y1 - y0));
                using Mat gray = new();
                Cv2.CvtColor(roi, gray, ColorConversionCodes.BGR2GRAY);
                Rect glyphs = new(box.X - x0, box.Y - y0, box.Width, box.Height);

                // ring = the patch without the glyph box, i.e. the plate around the text.
                using Mat ringMask = new(gray.Size(), MatType.CV_8UC1, Scalar.All(255));
                Cv2.Rectangle(ringMask, glyphs, Scalar.All(0), -1);
                int ringPixels = Cv2.CountNonZero(ringMask);
                if (ringPixels == 0) return false;

                // A name plate is dark and flat. A photo is neither, which is exactly what tells an
                // avatar apart from a name plate ("C0ol" read off an avatar with glasses).
                using Mat dark = new();
                Cv2.InRange(gray, Scalar.All(0), Scalar.All(170), dark);
                using Mat darkRing = new();
                Cv2.BitwiseAnd(dark, ringMask, darkRing);
                double darkShare = Cv2.CountNonZero(darkRing) / (double)ringPixels;

                Cv2.MeanStdDev(gray, out Scalar ringMean, out Scalar ringStd, ringMask);
                double plate = ringMean.Val0;
                if (plate >= 150 || darkShare < 0.75 || ringStd.Val0 > 42) return false;

                // The glyphs must sit *on* that plate colour with the background still showing between
                // them: a photo or a logo covers its box with pixels that are not the plate colour.
                using Mat boxMask = new(gray.Size(), MatType.CV_8UC1, Scalar.All(0));
                Cv2.Rectangle(boxMask, glyphs, Scalar.All(255), -1);
                int boxPixels = Cv2.CountNonZero(boxMask);
                if (boxPixels == 0) return false;

                using Mat plateBand = new();
                Cv2.InRange(gray, Scalar.All(Math.Max(0, plate - 32)), Scalar.All(Math.Min(255, plate + 32)), plateBand);
                using Mat onPlate = new();
                Cv2.BitwiseAnd(plateBand, boxMask, onPlate);
                return Cv2.CountNonZero(onPlate) / (double)boxPixels >= 0.45;
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
            if (digits > 0 && letters <= 2 && LooksLikeAmount(t)) return false;   // "213 BB", "2043BB", "1 BB"
            // "Total pot 1.5", "Play carefully", "Hand 2435663790" - a reading whose every word is client
            // chrome (numbers and lone punctuation count as chrome here, the pot and the hand id are numbers).
            string[] words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 0 && words.All(w => StopWords.Contains(w) ||
                                                    w.All(c => !char.IsLetterOrDigit(c)) ||
                                                    w.All(char.IsDigit))) return false;
            if (t.Length <= 2 && !t.Any(c => c > 127) && letters < 2) return false;
            return true;
        }

        /// <summary>
        /// True when a reading is a number with an optional unit behind it ("151.7", "213 BB", "20bb", "5 K").
        /// The stack and the bets are printed right under the name, so those readings have to be recognised
        /// as amounts - they used to become grey boxes for the numbers under a name.
        /// </summary>
        private static bool LooksLikeAmount(string text)
        {
            int i = 0;
            bool digit = false;
            while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.' || text[i] == ',' || text[i] == ' '))
            {
                if (char.IsDigit(text[i])) digit = true;
                i++;
            }
            if (!digit) return false;

            string tail = text[i..].Trim().ToUpperInvariant();
            return tail.Length == 0 || tail is "BB" or "B" or "K" or "KK" or "USD" or "EUR" or "SEK" or "NOK" or
                   "DKK" or "$" or "€" or "£" or "KR";
        }

        /// <summary>Joins words that sit on the same line: OCR often splits a name in two.</summary>
        private static List<SeatBox> MergeWords(List<Word> words, int frameWidth, int frameHeight, string tableTitle)
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

                // A group that grew much taller than one text line is not a name plate (a bet chip and the
                // dealer button next to the felt get merged into one blob, which then reads as "set"). The
                // plate pass finds the real name plates on its own anyway.
                if (box.Height > frameHeight * 0.06) continue;
                if (box.Width > frameWidth * 0.30) continue;

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

        /// <summary>
        /// Reads one line of text and reports Tesseract's own confidence for it (used by "Snip Player").
        /// The crop is upscaled so the glyphs are around 150 px tall.
        /// </summary>
        public static string ReadSingleLine(Mat bgr, TesseractEngine engine, out float confidence) =>
            ReadSingleLine(bgr, engine, out confidence, Math.Clamp(150.0 / Math.Max(1, bgr.Height), 2.0, 8.0));

        /// <summary>
        /// Reads one line of text with a caller chosen upscale factor. Which factor reads a name plate best
        /// differs per window (a very large upscale blurs small glyphs into mush for one font and is exactly
        /// what another one needs), so the callers try a couple of factors and keep the best reading.
        /// </summary>
        public static string ReadSingleLine(Mat bgr, TesseractEngine engine, out float confidence, double scale)
        {
            confidence = 0;
            try
            {
                // A crop of a pixel or two makes the native Tesseract build abort the whole process
                // (leptonica: "Image too small to scale"), so nothing that small is handed to it.
                if (bgr == null || bgr.Empty() || bgr.Width < 6 || bgr.Height < 6)
                {
                    PvLog.Throttled("TinyCrop", $"[OCR] skipped a {bgr?.Width ?? 0}x{bgr?.Height ?? 0} crop", 60);
                    return "";
                }

                scale = Math.Clamp(scale, 1.2, 9.0);

                // A crop that is (nearly) one flat colour has nothing to read, and the native Tesseract
                // build can abort the whole process on a degenerate text line in such an image
                // (leptonica: "Image too small to scale"), so those are never handed over.
                using Mat grayCheck = new();
                Cv2.CvtColor(bgr, grayCheck, ColorConversionCodes.BGR2GRAY);
                Cv2.MeanStdDev(grayCheck, out _, out Scalar spread);
                if (spread.Val0 < 6) return "";

                using Mat big = new();
                Cv2.Resize(bgr, big, new Size(0, 0), scale, scale, InterpolationFlags.Cubic);
                byte[] png;
                Cv2.ImEncode(".png", big, out png);

                lock (OcrLock)
                {
                    using Pix pix = Pix.LoadFromMemory(png);
                    using Page page = engine.Process(pix, PageSegMode.SingleLine);
                    confidence = page.GetMeanConfidence() * 100f;
                    return (page.GetText() ?? "").Replace("\n", " ").Trim();
                }
            }
            catch (Exception ex)
            {
                PvLog.Error("TableScanner.ReadSingleLine", ex);
                return "";
            }
        }

        /// <summary>
        /// The player name in a small patch around a point (used by "Snip Player"). Only a name plate
        /// reading that really looks like a player name is accepted, which is what keeps a click on the
        /// felt, a chip stack or an avatar from ending in "create the player '‘'".
        /// </summary>
        public static string ReadNameNear(Mat crop, int px, int py, TesseractEngine engine, out float confidence) =>
            ReadNameNear(crop, px, py, engine, out confidence, out _);

        /// <summary>
        /// The same reading, plus the rectangle (inside <paramref name="crop"/>) that produced it, so a hover
        /// box can be put exactly on the name that was read.
        /// </summary>
        public static string ReadNameNear(Mat crop, int px, int py, TesseractEngine engine, out float confidence,
                                          out Rect namePlate)
        {
            confidence = 0;
            namePlate = default;
            if (crop == null || crop.Empty()) return "";

            // The click is rarely exactly on the text row. The tight crop around the glyphs on the rows under
            // the cursor is the best guess, followed by strips of the name line height around them.
            List<Rect> crops = new();
            Rect found = default;
            int half = Math.Clamp(crop.Height / 4, 14, 30);
            foreach (int offset in new[] { 0, -13, 13 })
            {
                int y = py + offset;
                if (y < 0 || y >= crop.Height) continue;

                if (FindTextLine(crop, px, y, out Rect strip) &&
                    strip.X > 1 && strip.Right < crop.Width - 1 && strip.Y > 0 && strip.Bottom < crop.Height)
                    crops.Add(Pad(strip, crop.Size(), 4, 4));
            }

            foreach (int offset in new[] { 0, -13, 13 })
            {
                int y0 = Math.Clamp(py + offset - half, 0, Math.Max(0, crop.Height - 2));
                int y1 = Math.Clamp(y0 + half * 2, Math.Min(crop.Height, y0 + 8), crop.Height);
                if (y1 - y0 >= 16) crops.Add(new Rect(0, y0, crop.Width, y1 - y0));
            }

            if (crops.Count == 0)
            {
                PvLog.Throttled("SnipNoText", "[SNIP] nothing to read under the cursor", 15);
                return "";
            }

            // Only a reading that looks like a name *and* is confident enough is used: a strip through the
            // middle of a name plate reads half letters ("ol T P"), and that must never become a player.
            // Every band is read and the most confident name-like reading wins.
            string best = BestRead(crop, crops, engine, out confidence, 50, 0, r => found = r);
            namePlate = found;
            if (best.Length == 0)
            {
                PvLog.Throttled("SnipNoName", "[SNIP] no player name under the cursor", 15);
                return "";
            }

            PvLog.Throttled("SnipRead", $"[SNIP] cursor read -> '{best}' (conf {confidence:0})", 20);
            return best;
        }

        /// <summary>
        /// Reads the player name inside a marked area ("Snip Player" with a dragged rectangle).
        ///
        /// A mark is rarely exact: it usually covers the name and the stack below it, and on a light table it
        /// can cover a good part of the felt as well. So the lines of text inside the mark are searched for
        /// (each of them on its own - reading the name and the stack as one image gives nonsense), and the
        /// plate backed line comes first. The mark itself is the last resort.
        /// </summary>
        public static string ReadNameIn(Mat crop, TesseractEngine engine, out float confidence) =>
            ReadNameIn(crop, engine, out confidence, out _);

        /// <summary>
        /// The same reading, plus the rectangle (inside <paramref name="crop"/>) that produced it, so a hover
        /// box can be put exactly on the name that was read instead of over the whole mark.
        /// </summary>
        public static string ReadNameIn(Mat crop, TesseractEngine engine, out float confidence, out Rect namePlate)
        {
            confidence = 0;
            namePlate = default;
            if (crop == null || crop.Empty()) return "";

            List<Rect> crops = new();
            Rect found = default;
            foreach (Rect line in FindTextLines(crop)) crops.Add(Pad(line, crop.Size(), 2, 2));
            crops.Add(new Rect(0, 0, crop.Width, crop.Height));

            // No early stop: every line in the mark is read and the most confident name-like reading wins.
            // Stopping at the first line that only *sounds* confident is what made a mark over the name and
            // the stack below it read the stack ("02 2 RR" instead of "Rupshaw").
            string best = BestRead(crop, crops, engine, out confidence, 25, 0, r => found = r);
            namePlate = found;
            if (best.Length == 0)
            {
                PvLog.Throttled("SnipUnreadable", "[SNIP] nothing name like in the marked area", 15);
                return "";
            }

            PvLog.Throttled("SnipRead", $"[SNIP] marked area -> '{best}' (conf {confidence:0})", 20);
            return best;
        }

        /// <summary>
        /// The lines of light text in an image: each candidate on its own, the plate backed ones first and
        /// within those the one nearest to the middle of the image first (the user marks or points at the name,
        /// and the stack size is printed right under it), at most four. A bright area that fills a good part
        /// of the image is not a line of text - on a light table theme the felt itself is bright, and it used
        /// to be read as text.
        /// </summary>
        private static List<Rect> FindTextLines(Mat bgr)
        {
            List<Rect> plateLines = new();
            List<Rect> otherLines = new();
            try
            {
                double area = bgr.Width * (double)bgr.Height;
                double cx = bgr.Width / 2.0, cy = bgr.Height / 2.0;
                foreach (Rect r in BrightLineRects(bgr))
                {
                    if (!LooksLikeTextLine(r, area)) continue;                   // a bright patch, not a line
                    if (IsOnDarkPlate(bgr, r)) plateLines.Add(r);
                    else otherLines.Add(r);
                }

                double Distance(Rect r) =>
                    Math.Abs(r.X + r.Width / 2.0 - cx) + Math.Abs(r.Y + r.Height / 2.0 - cy) - r.Width / 4.0;

                plateLines.Sort((a, b) => Distance(a).CompareTo(Distance(b)));
                otherLines.Sort((a, b) => Distance(a).CompareTo(Distance(b)));
            }
            catch (Exception ex)
            {
                PvLog.Error("TableScanner.FindTextLines", ex);
            }

            return plateLines.Concat(otherLines).Take(4).ToList();
        }

        /// <summary>
        /// True when a bright rectangle is a line of text and not a bright patch of the table: the felt of some
        /// themes is almost white, and a mark or the patch around the cursor can cover a lot of it. A line of
        /// text is flat (much wider than tall) and never fills most of the image.
        /// </summary>
        private static bool LooksLikeTextLine(Rect r, double imageArea)
        {
            double share = r.Width * (double)r.Height / Math.Max(1.0, imageArea);
            if (share > 0.6) return false;
            return r.Width >= r.Height * 2.0 || share <= 0.25;
        }

        /// <summary>The rectangles of the light text in an image, filtered only by their shape.</summary>
        private static List<Rect> BrightLineRects(Mat bgr)
        {
            List<Rect> found = new();
            using Mat gray = new();
            Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
            using Mat bright = new();
            Cv2.InRange(gray, Scalar.All(150), Scalar.All(255), bright);     // light text on a dark plate
            using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(9, 3));
            using Mat joined = new();
            Cv2.MorphologyEx(bright, joined, MorphTypes.Close, kernel);

            Cv2.FindContours(joined, out Point[][] contours, out _,
                RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            foreach (Point[] c in contours)
            {
                Rect r = Cv2.BoundingRect(c);
                if (r.Height < 6 || r.Height > 46) continue;                     // a text line, not a shape
                if (r.Width < 6 || r.Width > bgr.Width * 0.9) continue;
                found.Add(r);
            }
            return found;
        }

        /// <summary>
        /// The line of light text around a point, or the whole image when <paramref name="px"/>/<paramref name="py"/>
        /// are negative. Returns false when there is nothing text like to read.
        /// </summary>
        private static bool FindTextLine(Mat bgr, int px, int py, out Rect strip)
        {
            strip = default;
            bool nearPoint = px >= 0 && py >= 0;
            try
            {
                double area = bgr.Width * (double)bgr.Height;
                List<Rect> lines = new();
                foreach (Rect r in BrightLineRects(bgr))
                {
                    // On a light table theme the felt is bright as well: a bright patch that covers a good
                    // part of the crop is the background, not the text on it.
                    if (!LooksLikeTextLine(r, area)) continue;
                    if (nearPoint && (py < r.Y - 14 || py > r.Bottom + 14)) continue;   // not the row under the cursor
                    if (nearPoint && (px < r.X - 80 || px > r.Right + 80)) continue;    // not the name next to it
                    lines.Add(r);
                }

                if (lines.Count == 0) return false;

                // Lines that really sit on a name plate describe the name; when there is none (a plate that is
                // not dark, a window chrome row) the plain lines are used.
                List<Rect> backed = lines.Where(r => IsOnDarkPlate(bgr, r)).ToList();
                List<Rect> used = backed.Count > 0 ? backed : lines;

                Rect box = used[0];
                foreach (Rect r in used.Skip(1)) box = box.Union(r);

                if (box.Width < 6 || box.Height < 6) return false;
                strip = box;
                return true;
            }
            catch (Exception ex)
            {
                PvLog.Error("TableScanner.FindTextLine", ex);
                return false;
            }
        }

        /// <summary>Grows a rectangle by a margin and keeps it inside the image.</summary>
        private static Rect Pad(Rect rect, Size image, int marginX, int marginY)
        {
            int x = Math.Max(0, rect.X - marginX);
            int y = Math.Max(0, rect.Y - marginY);
            int right = Math.Min(image.Width, rect.Right + marginX);
            int bottom = Math.Min(image.Height, rect.Bottom + marginY);
            return new Rect(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y));
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

        /// <summary>
        /// Best matching database key for an OCR reading ("" when nothing is close enough).
        ///
        /// Names that only look alike must never be swapped ("JimSteel" is not "JimSteele"), so the
        /// match is resolved in steps and gives up whenever the reading is ambiguous:
        ///   1. the same name ignoring case - a real player always beats a look-alike;
        ///   2. a candidate inside the OCR tolerance, but only when it is the single best one;
        ///   3. never when two players are equally close, and never when the only difference is a
        ///      character more or less at the end ("JimSteel" vs a lone "JimSteele").
        /// A grey box costs nothing; a note written on the wrong player does. Ambiguity is logged, so
        /// the reason for a grey box can always be looked up.
        /// </summary>
        public static (string Key, double Distance) MatchPlayer(string ocrText, Dictionary<string, string> normalizedKeys)
        {
            if (normalizedKeys == null || normalizedKeys.Count == 0) return ("", double.MaxValue);

            string raw = (ocrText ?? "").Trim();
            string norm = NormalizeName(raw);
            if (norm.Length < 3) return ("", double.MaxValue);

            // 1) the very same name (ignoring case)
            foreach (KeyValuePair<string, string> kv in normalizedKeys)
                if (string.Equals((kv.Key ?? "").Trim(), raw, StringComparison.OrdinalIgnoreCase))
                    return (kv.Key ?? "", 0);

            // 2) every player the OCR tolerance allows, keeping only the closest ones
            double best = double.MaxValue;
            List<KeyValuePair<string, string>> winners = new();
            foreach (KeyValuePair<string, string> kv in normalizedKeys)
            {
                string candidate = kv.Value ?? "";
                if (candidate.Length < 3) continue;

                // Long names get one edit more, because that is where a client font and a small plate cost
                // letters ("Sistahumlan" read as "'Sig;ahumlan"). Digits are never tolerated: a name with
                // other digits is a different player ("madmax789" is not "madmax717").
                int allowed = Math.Max(1, candidate.Length / 6);
                if (candidate.Length >= 10 && DigitsOf(candidate) == DigitsOf(norm)) allowed = 2;

                int d = candidate == norm ? 0 : Levenshtein(norm, candidate, allowed + 1);
                if (d > allowed) continue;

                if (d < best) { best = d; winners.Clear(); winners.Add(kv); }
                else if (d == best) winners.Add(kv);
            }

            if (winners.Count == 0) return ("", double.MaxValue);

            // 3) two players that look equally like the reading: never guess between them
            if (winners.Count > 1)
            {
                PvLog.Throttled("MatchAmbiguous", $"[OCR] '{raw}' fits " +
                    string.Join(" / ", winners.Select(w => w.Key)) + " equally well -> left unknown", 30);
                return ("", double.MaxValue);
            }

            KeyValuePair<string, string> winner = winners[0];
            // A character more or less at the end is what separates "JimSteel" from "JimSteele". When a
            // *second* player in the database also fits the reading that way, the reading is not trusted -
            // but a single name that is merely read with one character too few/many is still used, so a
            // player who *is* in the database never disappears from the screen for that reason.
            if (best > 0 && HasTrailingTwin(norm, winner.Value, normalizedKeys))
            {
                PvLog.Throttled("MatchNeighbour", $"[OCR] '{raw}' looks like '{winner.Key}' but one character " +
                                                  "differs and a second player looks the same -> left unknown", 30);
                return ("", double.MaxValue);
            }

            return (winner.Key, best);
        }

        /// <summary>The digits of a normalised name, used so a mismatch in digits is never tolerated.</summary>
        private static string DigitsOf(string normalized) => new(normalized.Where(char.IsDigit).ToArray());

        /// <summary>
        /// True when the reading differs from <paramref name="winner"/> only at the end *and* another
        /// player in the database fits the same reading the same way (the JimSteel/JimSteele case).
        /// </summary>
        private static bool HasTrailingTwin(string reading, string winner, Dictionary<string, string> normalizedKeys)
        {
            if (!reading.StartsWith(winner, StringComparison.Ordinal) &&
                !winner.StartsWith(reading, StringComparison.Ordinal)) return false;

            foreach (KeyValuePair<string, string> kv in normalizedKeys)
            {
                string other = kv.Value ?? "";
                if (other.Length < 3 || other == winner) continue;
                if (other.StartsWith(reading, StringComparison.Ordinal) ||
                    reading.StartsWith(other, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// True when a reading can be used as a player name: at least three letters/digits with two
        /// letters in them. Keeps "Snip Player" from offering to create a player out of whatever the
        /// OCR found in a graphic (a "?" or a quote on a name plate).
        /// </summary>
        public static bool LooksLikePlayerName(string raw)
        {
            string t = Clean(raw);
            if (!LooksLikeName(t)) return false;
            return t.Count(char.IsLetter) >= 2 && NormalizeName(t).Length >= 3;
        }

        /// <summary>
        /// The player a *rejected* reading resembles, for the hint in the note editor ("" when there is
        /// none). Used so a name read one character off does not silently create a duplicate player.
        /// </summary>
        public static string FindSimilarName(string ocrText, Dictionary<string, string> normalizedKeys)
        {
            if (normalizedKeys == null || normalizedKeys.Count == 0) return "";

            string norm = NormalizeName(ocrText);
            if (norm.Length < 3) return "";

            string bestKey = "";
            int best = int.MaxValue;
            bool tie = false;
            foreach (KeyValuePair<string, string> kv in normalizedKeys)
            {
                string candidate = kv.Value ?? "";
                if (candidate.Length < 3 || candidate == norm) continue;

                int d = Levenshtein(norm, candidate, 3);
                if (d > 2) continue;
                if (d < best) { best = d; bestKey = kv.Key; tie = false; }
                else if (d == best) tie = true;
            }
            return tie ? "" : bestKey;
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
