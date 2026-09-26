using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PokerNoteManager.Vision;
using Tesseract;

namespace PokerNoteManager
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private const string SingleInstanceMutexName = @"Local\PokerNotes_SingleInstance";
        private Mutex? _singleInstanceMutex;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectoryW(string path);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string path);

        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
        [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

        private const byte VK_CONTROL = 0x11;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

        private const int SW_RESTORE = 9;

        protected override void OnStartup(StartupEventArgs e)
        {
            PrepareNativePaths();

            // Support switch: PokerVisionHUD.exe --selftest-ocr  -> writes %LocalAppData%\PokerVisionHUD\selftest.txt
            if (e.Args.Any(a => a.Equals("--selftest-ocr", StringComparison.OrdinalIgnoreCase)))
            {
                RunOcrSelfTest();
                Environment.Exit(0);
                return;
            }

            // Support switch: PokerVisionHUD.exe --selftest-note -> builds the in-table note editor
            if (e.Args.Any(a => a.Equals("--selftest-note", StringComparison.OrdinalIgnoreCase)))
            {
                RunNotePopupSelfTest();
                Environment.Exit(0);
                return;
            }

            // Support switch: PokerVisionHUD.exe --selftest-ui -> renders the main window to a PNG
            if (e.Args.Any(a => a.Equals("--selftest-ui", StringComparison.OrdinalIgnoreCase)))
            {
                RunUiSelfTest();
                Environment.Exit(0);
                return;
            }

            // Support switch: PokerVisionHUD.exe --selftest-overlay -> renders the hover boxes to a PNG
            if (e.Args.Any(a => a.Equals("--selftest-overlay", StringComparison.OrdinalIgnoreCase)))
            {
                RunOverlaySelfTest();
                Environment.Exit(0);
                return;
            }

            // Support switch: PokerVisionHUD.exe --selftest-mouse -> checks the mouse hook buttons
            if (e.Args.Any(a => a.Equals("--selftest-mouse", StringComparison.OrdinalIgnoreCase)))
            {
                RunMouseSelfTest();
                Environment.Exit(0);
                return;
            }

            // Support switch: PokerVisionHUD.exe --selftest-screens -> boxes on every monitor
            if (e.Args.Any(a => a.Equals("--selftest-screens", StringComparison.OrdinalIgnoreCase)))
            {
                RunScreenSelfTest();
                Environment.Exit(0);
                return;
            }

            // --- Single instance guard: two copies would fight over the notes file ---
            _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out bool isFirstInstance);
            if (!isFirstInstance)
            {
                FocusExistingInstance();
                try { _singleInstanceMutex.Dispose(); } catch { }
                _singleInstanceMutex = null;
                Shutdown();
                return;
            }

            RegisterGlobalExceptionHandlers();

            base.OnStartup(e);

            try
            {
                MainWindow window = new PokerNoteManager.MainWindow();
                MainWindow = window;
                window.Show();
                PvLog.Write($"[APP] Poker Notes started. Log: {PvLog.LogFilePath}");
            }
            catch (Exception ex)
            {
                PvLog.Error("App.OnStartup creating MainWindow", ex);
                MessageBox.Show("Poker Notes could not start:\r\n" + ex.Message, "Poker Notes", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
            try { _singleInstanceMutex?.Dispose(); } catch { }
            base.OnExit(e);
        }

        /// <summary>
        /// A self extracting single file app runs from %TEMP%\.net\...\, so AppContext.BaseDirectory is
        /// NOT the folder the user sees. Tesseract's native loader looks for "x64\*.dll" relative to
        /// the app base / current directory, so the exe folder is made the current directory, added to
        /// the DLL search path, and its x64 natives are mirrored into the extraction folder.
        /// </summary>
        private static void PrepareNativePaths()
        {
            try
            {
                string exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
                if (exeDir.Length == 0) return;

                Directory.SetCurrentDirectory(exeDir);
                SetDllDirectoryW(Path.Combine(exeDir, "x64"));

                // Tesseract's native loader (InteropDotNet) resolves "x64\tesseract50.dll" from
                // Assembly.Location, which is EMPTY in a single file app, so it fails with
                // "Value cannot be null (Parameter 'path1')". Loading the libraries ourselves first
                // makes the later LoadLibrary("tesseract50.dll") return the already loaded module.
                foreach (string name in new[] { "leptonica-1.82.0.dll", "tesseract50.dll" })
                {
                    string path = Path.Combine(exeDir, "x64", name);
                    try
                    {
                        if (!File.Exists(path)) continue;
                        IntPtr handle = LoadLibraryW(path);
                        PvLog.Write($"[APP] preloaded native '{name}' -> {(handle == IntPtr.Zero ? "FAILED" : "ok")}");
                    }
                    catch (Exception ex) { PvLog.Error("PreloadNative " + name, ex); }
                }

                string src = Path.Combine(exeDir, "x64");
                string dst = Path.Combine(AppContext.BaseDirectory, "x64");
                if (!Directory.Exists(src) || string.Equals(src, dst, StringComparison.OrdinalIgnoreCase)) return;

                Directory.CreateDirectory(dst);
                foreach (string file in Directory.GetFiles(src, "*.dll"))
                {
                    string target = Path.Combine(dst, Path.GetFileName(file));
                    try
                    {
                        if (!File.Exists(target)) File.Copy(file, target, true);
                    }
                    catch (Exception ex) { PvLog.Error("PrepareNativePaths copy " + file, ex); }
                }
                PvLog.Write($"[APP] native folder mirrored for the OCR loader: {dst}");
            }
            catch (Exception ex) { PvLog.Error("PrepareNativePaths", ex); }
        }

        /// <summary>
        /// Checks the name rules that took the most tuning: which readings are not names at all (the stack
        /// size and the pot line under/above a name), which readings still match a database player (a client
        /// font costs letters, but a different digit is a different player) and that a marked area is read.
        /// </summary>
        private static string NameRuleSelfTest()
        {
            List<string> problems = new();

            foreach (string notAName in new[] { "213 BB", "100 BB", "2043BB", "1 BB", "151.7 BB", "Total pot 1", "Hand 2435663790" })
                if (TableScanner.LooksLikePlayerName(notAName)) problems.Add($"'{notAName}' was taken for a name");

            foreach (string name in new[] { "Sistahumlan", "ruhhy", "Ez-E", "7up", "madmax789", "C0ol" })
                if (!TableScanner.LooksLikePlayerName(name)) problems.Add($"'{name}' was not taken for a name");

            Dictionary<string, string> keys = new(StringComparer.OrdinalIgnoreCase)
            {
                { "Sistahumlan", TableScanner.NormalizeName("Sistahumlan") },
                { "madmax717", TableScanner.NormalizeName("madmax717") },
                { "JimSteel", TableScanner.NormalizeName("JimSteel") }
            };
            if (TableScanner.MatchPlayer("'Sig;ahumlan", keys).Key != "Sistahumlan")
                problems.Add("the reading ''Sig;ahumlan' did not match 'Sistahumlan'");
            if (TableScanner.MatchPlayer("Sistahumlan", keys).Key != "Sistahumlan")
                problems.Add("'Sistahumlan' did not match itself");
            if (TableScanner.MatchPlayer("madmax789", keys).Key.Length != 0)
                problems.Add("'madmax789' matched 'madmax717' (other digits)");
            if (TableScanner.MatchPlayer("JimSteele", keys).Key != "JimSteel")
                problems.Add("'JimSteele' did not match 'JimSteel'");
            if (TableScanner.MatchPlayer("xXx_Pro_xXx", keys).Key.Length != 0)
                problems.Add("a name that is in no database matched one");

            // The marked area of "Snip Player": a mark over the name and the stack below it has to give the
            // name, not the stack size.
            using (OpenCvSharp.Mat plate = RenderNamePlate("Sistahumlan", "151.7 BB"))
            {
                using TesseractEngine engine = TableScanner.CreateEngine();
                string marked = TableScanner.ReadNameIn(plate, engine, out float confidence);
                if (!marked.Contains("humlan", StringComparison.OrdinalIgnoreCase))
                    problems.Add($"the marked area gave '{marked}' (conf {confidence:0})");

                string near = TableScanner.ReadNameNear(plate, plate.Width / 2, plate.Height / 2 - 12, engine, out float nearConf);
                if (!near.Contains("humlan", StringComparison.OrdinalIgnoreCase))
                    problems.Add($"the click under the cursor gave '{near}' (conf {nearConf:0})");
            }

            return problems.Count == 0 ? "OK - 20 reading checks" : string.Join("; ", problems);
        }

        /// <summary>
        /// A name plate as the clients draw it: light text on a dark plate, with the stack size right under
        /// it (the row that used to be read instead of the name). Drawn 4x and scaled down so the text is
        /// crisp like real client text.
        /// </summary>
        private static OpenCvSharp.Mat RenderNamePlate(string name, string stack)
        {
            const int scale = 4, w = 220, h = 60;
            DrawingVisual visual = new();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x12, 0x18, 0x24)), null,
                                 new System.Windows.Rect(0, 0, w * scale, h * scale));
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x24, 0x2C, 0x3A)), null,
                                 new System.Windows.Rect(20 * scale, 12 * scale, 180 * scale, 40 * scale));

                FormattedText nameText = new(name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                             new Typeface("Segoe UI"), 9 * scale, Brushes.White, 96);
                dc.DrawText(nameText, new System.Windows.Point(26 * scale, 15 * scale));

                FormattedText stackText = new(stack, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                              new Typeface("Segoe UI"), 9 * scale,
                                              new SolidColorBrush(Color.FromRgb(0x4A, 0xD6, 0x6A)), 96);
                dc.DrawText(stackText, new System.Windows.Point(26 * scale, 33 * scale));
            }

            RenderTargetBitmap big = new(w * scale, h * scale, 96, 96, PixelFormats.Pbgra32);
            big.Render(visual);
            byte[] buffer = new byte[w * scale * h * scale * 4];
            big.CopyPixels(buffer, w * scale * 4, 0);
            using OpenCvSharp.Mat bgra = new(h * scale, w * scale, OpenCvSharp.MatType.CV_8UC4);
            Marshal.Copy(buffer, 0, bgra.Data, buffer.Length);
            using OpenCvSharp.Mat bgr = new();
            OpenCvSharp.Cv2.CvtColor(bgra, bgr, OpenCvSharp.ColorConversionCodes.BGRA2BGR);

            OpenCvSharp.Mat small = new();
            OpenCvSharp.Cv2.Resize(bgr, small, new OpenCvSharp.Size(w, h), 0, 0, OpenCvSharp.InterpolationFlags.Area);
            return small;
        }

        /// <summary>Writes %LocalAppData%\PokerVisionHUD\selftest.txt (see the --selftest-ocr switch).</summary>
        private static void RunOcrSelfTest()
        {
            StringBuilder report = new();
            report.AppendLine($"Poker Notes OCR self test   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine($"exe             : {Environment.ProcessPath}");
            report.AppendLine($"exe folder      : {Path.GetDirectoryName(Environment.ProcessPath ?? "")}");
            report.AppendLine($"app base dir    : {AppContext.BaseDirectory}");
            report.AppendLine($"current dir     : {Environment.CurrentDirectory}");
            report.AppendLine($"x64\tesseract50 : {File.Exists(Path.Combine(Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "", "x64", "tesseract50.dll"))}");
            report.AppendLine($"x64 in base dir : {File.Exists(Path.Combine(AppContext.BaseDirectory, "x64", "tesseract50.dll"))}");
            report.AppendLine("tessdata candidates:");
            foreach (string candidate in TableScanner.DescribeCandidates())
                report.AppendLine($"   {candidate}   folder={Directory.Exists(candidate)}  data={File.Exists(Path.Combine(candidate, "eng.traineddata"))}");

            try
            {
                using TesseractEngine engine = TableScanner.CreateEngine();
                report.AppendLine("RESULT OCR   : OK - the OCR engine started");
            }
            catch (Exception ex)
            {
                report.AppendLine("RESULT OCR   : FAILED");
                report.AppendLine("   " + ex.GetType().Name + ": " + ex.Message.Replace("\r\n", " ").Replace("\n", " "));
                Exception? inner = ex.InnerException;
                int depth = 0;
                while (inner != null && depth++ < 5)
                {
                    report.AppendLine("   caused by: " + inner.GetType().Name + ": " + inner.Message);
                    inner = inner.InnerException;
                }
                PvLog.Error("OcrSelfTest", ex);
            }

            try
            {
                // OpenCV is used for the felt detection: make sure it loads in the published build too.
                using OpenCvSharp.Mat probe = new(8, 8, OpenCvSharp.MatType.CV_8UC1, OpenCvSharp.Scalar.All(0));
                OpenCvSharp.Cv2.Rectangle(probe, new OpenCvSharp.Rect(1, 1, 4, 4), OpenCvSharp.Scalar.All(255), -1);
                int nonZero = OpenCvSharp.Cv2.CountNonZero(probe);
                report.AppendLine($"RESULT OpenCV: OK - version {OpenCvSharp.Cv2.GetVersionString()}, probe pixels {nonZero}");
            }
            catch (Exception ex)
            {
                report.AppendLine("RESULT OpenCV: FAILED");
                report.AppendLine("   " + ex.GetType().Name + ": " + ex.Message);
                Exception? inner = ex.InnerException;
                int depth = 0;
                while (inner != null && depth++ < 5)
                {
                    report.AppendLine("   caused by: " + inner.GetType().Name + ": " + inner.Message);
                    inner = inner.InnerException;
                }
                PvLog.Error("OcrSelfTest OpenCV", ex);
            }

            try
            {
                report.AppendLine("RESULT rules : " + NameRuleSelfTest());
            }
            catch (Exception ex)
            {
                report.AppendLine("RESULT rules : FAILED");
                report.AppendLine("   " + ex.GetType().Name + ": " + ex.Message);
                PvLog.Error("OcrSelfTest rules", ex);
            }

            try
            {
                Directory.CreateDirectory(PvLog.LogDirectory);
                File.WriteAllText(Path.Combine(PvLog.LogDirectory, "selftest.txt"), report.ToString());
            }
            catch { }

            PvLog.Write("[SELFTEST] " + report.ToString().Replace("\r\n", " | ").Replace("\n", " | "));
        }

        /// <summary>
        /// Builds and briefly shows the in-table note editor (writes the same selftest file).
        /// Used by the --selftest-note switch, so a broken layout or a missing resource is caught
        /// before the program is handed over.
        /// </summary>
        private static void RunNotePopupSelfTest()
        {
            StringBuilder report = new();
            report.AppendLine($"Poker Notes note popup self test   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            try
            {
                List<TagDef> tags = new()
                {
                    new TagDef { TagKey = "fish", DisplayName = "Fish", ColorHex = "#00008B" },
                    new TagDef { TagKey = "reg", DisplayName = "Reg", ColorHex = "#FF8C00" },
                    new TagDef { TagKey = "aggro", DisplayName = "Aggro", ColorHex = "#B22222" }
                };

                string? saved = null;
                Vision.NotePopup popup = new(
                    known: true,
                    playerName: "ruhhy",
                    note: "selftest note",
                    allTags: tags,
                    playerTags: new List<string> { "fish" },
                    onSave: (name, tagList, note) => saved = $"{name}|{string.Join(",", tagList)}|{note.Length}",
                    openInMain: () => { });

                popup.Left = 40;
                popup.Top = 40;
                popup.Show();
                report.AppendLine($"RESULT Popup : OK - shown {popup.ActualWidth:0}x{popup.ActualHeight:0}, " +
                                  $"title '{popup.Title}'");

                // Render the popup to a PNG so the layout (button text, tag chips) can be inspected.
                try
                {
                    int w = (int)Math.Ceiling(popup.ActualWidth), h = (int)Math.Ceiling(popup.ActualHeight);
                    System.Windows.Media.Imaging.RenderTargetBitmap bitmap =
                        new(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(popup);
                    System.Windows.Media.Imaging.PngBitmapEncoder encoder = new();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    string dir = Path.Combine(PvLog.LogDirectory, "debug_snapshots");
                    Directory.CreateDirectory(dir);
                    string file = Path.Combine(dir, "selftest_note_popup.png");
                    using FileStream stream = File.Create(file);
                    encoder.Save(stream);
                    report.AppendLine($"RESULT Render: {file}");
                }
                catch (Exception ex) { report.AppendLine("RESULT Render: FAILED - " + ex.Message); }

                // Press a tag chip and the Save button through the very same path a click uses, so the
                // wiring (name + tags + note -> callback) is proven and not just the layout.
                int chips = 0;
                if (popup.FindName("TagPanel") is System.Windows.Controls.Panel panel)
                {
                    foreach (object child in panel.Children)
                    {
                        chips++;
                        if (child is System.Windows.Controls.Button chip &&
                            (chip.Content as string)?.Contains("Reg") == true)
                            chip.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    }
                }

                if (popup.FindName("BtnSave") is System.Windows.Controls.Button save)
                    save.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

                report.AppendLine($"RESULT Chips : {chips} tag button(s) built");
                report.AppendLine($"RESULT Save  : {(saved == null ? "FAILED - the save callback was never called" : saved)}");
                popup.Close();
            }
            catch (Exception ex)
            {
                report.AppendLine("RESULT Popup : FAILED");
                report.AppendLine("   " + ex.GetType().Name + ": " + ex.Message);
                Exception? inner = ex.InnerException;
                int depth = 0;
                while (inner != null && depth++ < 5)
                {
                    report.AppendLine("   caused by: " + inner.GetType().Name + ": " + inner.Message);
                    inner = inner.InnerException;
                }
                PvLog.Error("NotePopupSelfTest", ex);
            }

            try
            {
                Directory.CreateDirectory(PvLog.LogDirectory);
                File.WriteAllText(Path.Combine(PvLog.LogDirectory, "selftest.txt"), report.ToString());
            }
            catch { }

            PvLog.Write("[SELFTEST] " + report.ToString().Replace("\r\n", " | ").Replace("\n", " | "));
        }

        /// <summary>
        /// Shows the main window and renders it to a PNG (used by --selftest-ui), so the layout can be
        /// checked without clicking through the program.
        /// </summary>
        private static void RunUiSelfTest()
        {
            StringBuilder report = new();
            report.AppendLine($"Poker Notes UI self test   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            // The window saves its size on close. That must not overwrite the user's real window setting.
            string statePath = NotesStore.WindowStatePath;
            byte[]? stateBackup = null;
            try { if (File.Exists(statePath)) stateBackup = File.ReadAllBytes(statePath); }
            catch { }

            try
            {
                MainWindow window = new();
                window.Show();
                window.UpdateLayout();

                string file = RenderToPng(window, "ui_main_window.png");
                report.AppendLine($"RESULT Window: OK - {window.ActualWidth:0}x{window.ActualHeight:0}, " +
                                  $"title '{window.Title}'");
                report.AppendLine($"RESULT Render: {file}");

                // The scan bar must show every button: report the laid out size of each one.
                foreach (string name in new[] { "BtnScan", "BtnPasteScan", "BtnSnip", "BtnBoxes", "BtnOnlyDb",
                                                "BtnClearBoxes", "BtnDebugShot", "BtnAddToTable", "BtnBoxHere",
                                                "ScreenPicker", "AutoScanPicker" })
                {
                    if (window.FindName(name) is System.Windows.FrameworkElement element)
                        report.AppendLine($"   {name,-16} {element.ActualWidth:0}x{element.ActualHeight:0} " +
                                          $"visibility={element.Visibility}");
                    else
                        report.AppendLine($"   {name,-16} NOT FOUND");
                }

                // Also prove the layout works on a small screen: shrink to the minimum size and render again.
                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                window.WindowState = System.Windows.WindowState.Normal;
                window.UpdateLayout();
                System.Threading.Thread.Sleep(250);
                window.UpdateLayout();
                string small = RenderToPng(window, "ui_main_window_small.png");
                report.AppendLine($"RESULT Small : OK - {window.ActualWidth:0}x{window.ActualHeight:0} -> {small}");
                foreach (string name in new[] { "BtnDebugShot", "BtnClearBoxes", "ScreenPicker", "AutoScanPicker" })
                    if (window.FindName(name) is System.Windows.FrameworkElement element)
                        report.AppendLine($"   small {name,-16} {element.ActualWidth:0}x{element.ActualHeight:0}");

                window.Close();
            }
            catch (Exception ex)
            {
                report.AppendLine("RESULT Window: FAILED");
                report.AppendLine("   " + ex.GetType().Name + ": " + ex.Message);
                Exception? inner = ex.InnerException;
                int depth = 0;
                while (inner != null && depth++ < 5)
                {
                    report.AppendLine("   caused by: " + inner.GetType().Name + ": " + inner.Message);
                    inner = inner.InnerException;
                }
                PvLog.Error("UiSelfTest", ex);
            }
            finally
            {
                try
                {
                    if (stateBackup != null) File.WriteAllBytes(statePath, stateBackup);
                    else if (File.Exists(statePath)) File.Delete(statePath);
                }
                catch { }
            }

            try
            {
                Directory.CreateDirectory(PvLog.LogDirectory);
                File.WriteAllText(Path.Combine(PvLog.LogDirectory, "selftest.txt"), report.ToString());
            }
            catch { }

            PvLog.Write("[SELFTEST] " + report.ToString().Replace("\r\n", " | ").Replace("\n", " | "));
        }

        /// <summary>
        /// Draws a known player with tags, a known player without tags and an unknown reading on the
        /// hover overlay and renders it to a PNG (used by --selftest-overlay).
        /// </summary>
        private static void RunOverlaySelfTest()
        {
            StringBuilder report = new();
            report.AppendLine($"Poker Notes hover overlay self test   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            try
            {
                List<Vision.ScreenInfo> screens = Vision.ScreenCapture.GetScreens();
                Vision.ScreenInfo screen = screens.Count > 0
                    ? screens[0]
                    : new Vision.ScreenInfo { Index = 0, Bounds = new OpenCvSharp.Rect(0, 0, 1280, 720) };

                List<Vision.SeatBox> boxes = new()
                {
                    new Vision.SeatBox { TableTitle = "test", OcrText = "kikkk1777", PlayerKey = "kikkk1777",
                                         Confidence = 90, ScreenRect = new OpenCvSharp.Rect(60, 90, 130, 30) },
                    new Vision.SeatBox { TableTitle = "test", OcrText = "ruhhy", PlayerKey = "ruhhy",
                                         Confidence = 92, ScreenRect = new OpenCvSharp.Rect(420, 180, 90, 30) },
                    new Vision.SeatBox { TableTitle = "test", OcrText = "newguy", PlayerKey = "",
                                         Confidence = 70, ScreenRect = new OpenCvSharp.Rect(780, 270, 100, 30) }
                };

                Vision.HoverOverlay overlay = new(screen, box =>
                {
                    if (box.PlayerKey == "kikkk1777")
                        return new Vision.HoverInfo
                        {
                            Known = true, Label = "kikkk1777", Title = "kikkk1777", Body = "Fish � Limper\ncalls too much",
                            MainColour = System.Windows.Media.Color.FromRgb(0xFF, 0x8C, 0x00),
                            Chips = new List<(string Text, System.Windows.Media.Color Colour)>
                            {
                                ("Fish", System.Windows.Media.Color.FromRgb(0x1E, 0x90, 0xFF)),
                                ("Limper", System.Windows.Media.Color.FromRgb(0xFF, 0x8C, 0x00))
                            }
                        };
                    if (box.PlayerKey == "ruhhy")
                        return new Vision.HoverInfo
                        {
                            Known = true, Label = "ruhhy", Title = "ruhhy", Body = "no tags\nregular",
                            MainColour = System.Windows.Media.Color.FromRgb(0x22, 0xC5, 0x5E),
                            Chips = Array.Empty<(string, System.Windows.Media.Color)>()
                        };
                    return new Vision.HoverInfo
                    {
                        Known = false, Label = "newguy", Title = "? newguy",
                        Body = "Not in the database yet.",
                        MainColour = System.Windows.Media.Color.FromRgb(0x94, 0xA3, 0xB8),
                        Chips = Array.Empty<(string, System.Windows.Media.Color)>()
                    };
                });

                overlay.Show();
                overlay.SetBoxes(boxes);
                overlay.UpdateLayout();
                string file = RenderToPng(overlay, "selftest_hover_overlay.png");
                report.AppendLine($"RESULT Overlay: OK - {boxes.Count} box(es) drawn on display {screen.Index + 1}");
                report.AppendLine($"RESULT Render : {file}");
                overlay.ClearBoxes();
                overlay.Close();
            }
            catch (Exception ex)
            {
                report.AppendLine("RESULT Overlay: FAILED");
                report.AppendLine("   " + ex.GetType().Name + ": " + ex.Message);
                PvLog.Error("OverlaySelfTest", ex);
            }

            try
            {
                Directory.CreateDirectory(PvLog.LogDirectory);
                File.WriteAllText(Path.Combine(PvLog.LogDirectory, "selftest.txt"), report.ToString());
            }
            catch { }

            PvLog.Write("[SELFTEST] " + report.ToString().Replace("\r\n", " | ").Replace("\n", " | "));
        }

        /// <summary>
        /// Installs the mouse hook and sends one synthetic right, left and middle click to a window of
        /// our own (used by --selftest-mouse). Verifies that hover, note editor, scan and box removal
        /// all get their mouse events. The cursor position is restored afterwards.
        /// </summary>
        private static void RunMouseSelfTest()
        {
            StringBuilder report = new();
            report.AppendLine($"Poker Notes mouse hook self test   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            int right = 0, left = 0, middle = 0, leftWithCtrl = 0, middleWithCtrl = 0, dragStarted = 0, dragEnded = 0;
            System.Windows.Window? target = null;
            Vision.GlobalMouseHook? hook = null;
            GetCursorPos(out POINT saved);

            try
            {
                // The clicks must land on something harmless: our own empty window.
                target = new System.Windows.Window
                {
                    Title = "mouse hook selftest",
                    Width = 320,
                    Height = 220,
                    WindowStyle = System.Windows.WindowStyle.None,
                    ShowInTaskbar = false,
                    Background = System.Windows.Media.Brushes.DarkSlateGray,
                    Content = new System.Windows.Controls.TextBlock
                    {
                        Text = "Poker Notes mouse self test",
                        Foreground = System.Windows.Media.Brushes.White,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        VerticalAlignment = System.Windows.VerticalAlignment.Center
                    }
                };
                target.Show();
                target.UpdateLayout();
                SetCursorPos((int)target.Left + 160, (int)target.Top + 110);

                hook = new Vision.GlobalMouseHook();
                hook.RightClicked += (x, y) => right++;
                hook.LeftClicked += (x, y, ctrl) => { if (ctrl) leftWithCtrl++; else left++; };
                hook.MiddleClicked += (x, y, ctrl) => { if (ctrl) middleWithCtrl++; else middle++; };
                hook.DragStarted += (x, y) => dragStarted++;
                hook.DragEnded += (x, y) => dragEnded++;
                hook.Start();
                report.AppendLine($"RESULT Hook  : {(hook.IsRunning ? "installed" : "FAILED - not installed")}");

                System.Windows.Threading.Dispatcher dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                System.Windows.Threading.DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
                int step = 0;
                timer.Tick += (_, _) =>
                {
                    step++;
                    switch (step)
                    {
                        case 1: mouse_event(MOUSEEVENTF_RIGHTDOWN | MOUSEEVENTF_RIGHTUP, 0, 0, 0, IntPtr.Zero); break;
                        case 2: mouse_event(MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero); break;
                        case 3: mouse_event(MOUSEEVENTF_MIDDLEDOWN | MOUSEEVENTF_MIDDLEUP, 0, 0, 0, IntPtr.Zero); break;
                        case 4:     // Ctrl + left click, the shortcut that removes one hover box
                            keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
                            mouse_event(MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
                            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                            break;
                        case 5:     // Ctrl + middle click, the shortcut that boxes a player by hand
                            keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
                            mouse_event(MOUSEEVENTF_MIDDLEDOWN | MOUSEEVENTF_MIDDLEUP, 0, 0, 0, IntPtr.Zero);
                            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                            break;
                        case 6:     // middle button down and dragged: that is how a box is moved
                            mouse_event(MOUSEEVENTF_MIDDLEDOWN, 0, 0, 0, IntPtr.Zero);
                            break;
                        case 7:
                            SetCursorPos((int)target.Left + 200, (int)target.Top + 150);
                            break;
                        case 8:
                            mouse_event(MOUSEEVENTF_MIDDLEUP, 0, 0, 0, IntPtr.Zero);
                            break;
                        default:
                            timer.Stop();
                            dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background);
                            break;
                    }
                };
                timer.Start();
                System.Windows.Threading.Dispatcher.Run();

                report.AppendLine($"RESULT Clicks: left={left} ctrl+left={leftWithCtrl} middle={middle} " +
                                  $"ctrl+middle={middleWithCtrl} right={right} drags={dragStarted}/{dragEnded}");
                report.AppendLine(left >= 1 && leftWithCtrl >= 1 && middle >= 1 && middleWithCtrl >= 1
                    ? "RESULT MouseHook: OK - plain, Ctrl and middle clicks all reach the program " +
                      $"({(dragStarted == dragEnded ? "drag events balanced" : "WARNING: drag events unbalanced")})"
                    : "RESULT MouseHook: FAILED - a click did not reach the program");
            }
            catch (Exception ex)
            {
                report.AppendLine("RESULT MouseHook: FAILED");
                report.AppendLine("   " + ex.GetType().Name + ": " + ex.Message);
                PvLog.Error("MouseSelfTest", ex);
            }
            finally
            {
                try { hook?.Dispose(); } catch { }
                try { target?.Close(); } catch { }
                SetCursorPos(saved.X, saved.Y);        // put the user's cursor back where it was
            }

            try
            {
                Directory.CreateDirectory(PvLog.LogDirectory);
                File.WriteAllText(Path.Combine(PvLog.LogDirectory, "selftest.txt"), report.ToString());
            }
            catch { }

            PvLog.Write("[SELFTEST] " + report.ToString().Replace("\r\n", " | ").Replace("\n", " | "));
        }

        /// <summary>
        /// Draws boxes on every attached screen and renders each overlay to a PNG (used by
        /// --selftest-screens). Shows whether the boxes land where they should on all monitors,
        /// including a monitor to the left of the primary one (negative coordinates).
        /// </summary>
        private static void RunScreenSelfTest()
        {
            StringBuilder report = new();
            report.AppendLine($"Poker Notes screens self test   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            try
            {
                List<Vision.ScreenInfo> screens = Vision.ScreenCapture.GetScreens();
                report.AppendLine($"RESULT Screens: {screens.Count} display(s)");
                report.AppendLine($"   virtual screen: {SystemParameters.VirtualScreenWidth:0}x{SystemParameters.VirtualScreenHeight:0} " +
                                  $"at {SystemParameters.VirtualScreenLeft:0},{SystemParameters.VirtualScreenTop:0} (WPF units)");
                if (screens.Count == 0) report.AppendLine("RESULT Overlay: FAILED - no screen found");

                foreach (Vision.ScreenInfo screen in screens)
                {
                    string tag = "display " + (screen.Index + 1);
                    report.AppendLine($"   {tag}: {screen.Bounds.Width}x{screen.Bounds.Height} " +
                                      $"at {screen.Bounds.X},{screen.Bounds.Y} (physical pixels)");

                    // Three boxes per screen: top left, middle and bottom right, each with its own text.
                    List<Vision.SeatBox> boxes = new()
                    {
                        MakeTestBox(screen, screen.Bounds.X + 40, screen.Bounds.Y + 40, tag + " top left"),
                        MakeTestBox(screen, screen.Bounds.X + screen.Bounds.Width / 2 - 80,
                                    screen.Bounds.Y + screen.Bounds.Height / 2, tag + " middle"),
                        MakeTestBox(screen, screen.Bounds.X + screen.Bounds.Width - 240,
                                    screen.Bounds.Y + screen.Bounds.Height - 70, tag + " bottom right")
                    };

                    Vision.HoverOverlay overlay = new(screen, box => new Vision.HoverInfo
                    {
                        Known = true,
                        Label = box.OcrText,
                        Title = box.OcrText,
                        Body = "screen self test",
                        MainColour = System.Windows.Media.Color.FromRgb(0x22, 0xC5, 0x5E)
                    });
                    overlay.Show();
                    overlay.SetBoxes(boxes);
                    overlay.UpdateLayout();

                    string file = RenderToPng(overlay, $"selftest_screen{screen.Index + 1}.png");
                    report.AppendLine($"      overlay dpi scale {overlay.DpiScale:0.00} -> {file}");
                    overlay.ClearBoxes();
                    overlay.Close();
                }

                report.AppendLine("RESULT Overlay: OK - boxes drawn and rendered on every screen");
            }
            catch (Exception ex)
            {
                report.AppendLine("RESULT Overlay: FAILED");
                report.AppendLine("   " + ex.GetType().Name + ": " + ex.Message);
                PvLog.Error("ScreenSelfTest", ex);
            }

            try
            {
                Directory.CreateDirectory(PvLog.LogDirectory);
                File.WriteAllText(Path.Combine(PvLog.LogDirectory, "selftest.txt"), report.ToString());
            }
            catch { }

            PvLog.Write("[SELFTEST] " + report.ToString().Replace("\r\n", " | ").Replace("\n", " | "));
        }

        private static Vision.SeatBox MakeTestBox(Vision.ScreenInfo screen, int x, int y, string label) =>
            new()
            {
                TableTitle = "screen test " + (screen.Index + 1),
                TableHandle = new IntPtr(9000 + screen.Index),
                OcrText = label,
                PlayerKey = label,
                Confidence = 90,
                ScreenRect = new OpenCvSharp.Rect(x, y, 170, 28)
            };

        /// <summary>Renders a window (or any visual) to a PNG inside the snapshots folder.</summary>
        private static string RenderToPng(System.Windows.Media.Visual visual, string fileName)
        {
            int w = Math.Max(1, (int)Math.Ceiling((visual as System.Windows.FrameworkElement)?.ActualWidth ?? 0));
            int h = Math.Max(1, (int)Math.Ceiling((visual as System.Windows.FrameworkElement)?.ActualHeight ?? 0));
            System.Windows.Media.Imaging.RenderTargetBitmap bitmap =
                new(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(visual);

            System.Windows.Media.Imaging.PngBitmapEncoder encoder = new();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));

            string dir = Path.Combine(PvLog.LogDirectory, "debug_snapshots");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, fileName);
            using FileStream stream = File.Create(file);
            encoder.Save(stream);
            return file;
        }

        private static void FocusExistingInstance()
        {
            try
            {
                string currentName = Process.GetCurrentProcess().ProcessName;
                foreach (Process p in Process.GetProcessesByName(currentName))
                {
                    if (p.Id == Environment.ProcessId) continue;
                    IntPtr handle = p.MainWindowHandle;
                    if (handle == IntPtr.Zero) continue;

                    ShowWindowAsync(handle, SW_RESTORE);
                    SetForegroundWindow(handle);
                    PvLog.Write("[APP] Second instance attempted to start - existing window brought to front.");
                    return;
                }
            }
            catch (Exception ex)
            {
                PvLog.Error("FocusExistingInstance", ex);
            }
        }

        private void RegisterGlobalExceptionHandlers()
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
                PvLog.Error("AppDomain.UnhandledException" + (args.IsTerminating ? " (terminating)" : ""), args.ExceptionObject as Exception);

            TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                PvLog.Error("TaskScheduler.UnobservedTaskException", args.Exception);
                args.SetObserved();
            };
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            PvLog.Error("DispatcherUnhandledException", e.Exception);

            try
            {
                MessageBoxResult choice = ModernMessageBox.Show(
                    "An unexpected error occurred:\r\n\r\n" + e.Exception.Message +
                    "\r\n\r\nThe full details were written to:\r\n" + PvLog.LogFilePath +
                    "\r\n\r\nDo you want to keep the program running? (Recommended: close and restart.)",
                    "Unexpected Error",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Error);

                if (choice == MessageBoxResult.Yes)
                {
                    e.Handled = true;
                    return;
                }

                // Close cleanly so pending database changes are flushed.
                e.Handled = true;
                try { MainWindow?.Close(); } catch { }
            }
            catch
            {
                // If even the dialog fails we let the exception terminate the process (it is logged).
            }
        }
    }

}
