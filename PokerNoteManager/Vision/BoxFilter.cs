using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PokerNoteManager.Vision
{
    /// <summary>
    /// Decides which hover boxes are drawn: the "only players from the database" switch and the
    /// single boxes the user removed by hand (right click) are both handled here.
    ///
    /// The same player can be seated at several tables at once, so a box is identified by *table plus
    /// seat*: removing the box on one table never removes the same player's box on another table.
    /// </summary>
    public static class BoxFilter
    {
        /// <summary>The table a box belongs to: the window handle when known, else the window title.</summary>
        public static string TableKey(SeatBox box) =>
            box.TableHandle != IntPtr.Zero
                ? "win" + box.TableHandle.ToInt64().ToString(CultureInfo.InvariantCulture)
                : "title:" + (box.TableTitle ?? "").Trim().ToLowerInvariant();

        /// <summary>
        /// A stable key for one box: the table it sits on plus the matched player, or the OCR reading
        /// when the name is not known yet. A reading that turns into a known player counts as a new box.
        /// </summary>
        public static string KeyFor(SeatBox box)
        {
            string seat = box.Matched
                ? "player:" + box.PlayerKey
                : "read:" + TableScanner.NormalizeName(box.OcrText);
            return TableKey(box) + "|" + seat;
        }

        /// <summary>True when the box should be drawn on the screen.</summary>
        public static bool IsVisible(SeatBox box, bool onlyDatabase, ICollection<string> hiddenKeys)
        {
            if (onlyDatabase && !box.Matched) return false;
            return !hiddenKeys.Contains(KeyFor(box));
        }

        /// <summary>How many different tables the same player is boxed on (1 when only one table).</summary>
        public static int TableCountFor(IEnumerable<SeatBox> boxes, string playerKey) =>
            boxes.Where(b => b.Matched && string.Equals(b.PlayerKey, playerKey, StringComparison.OrdinalIgnoreCase))
                 .Select(TableKey)
                 .Distinct(StringComparer.OrdinalIgnoreCase)
                 .Count();
    }
}
