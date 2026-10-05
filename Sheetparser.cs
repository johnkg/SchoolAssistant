using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SchoolAssistant;

/// <summary>One calendar-worthy cell (or run of identical adjacent cells) from the school tracker.</summary>
public sealed record SheetItem(string Gid, string Subject, DateOnly Start, DateOnly End, string Text, string CellRef)
{
    // Identity: tab + subject + first day. Editing the text keeps the identity; moving it to another day does not.
    public string Key => $"{Gid}|{Subject.ToUpperInvariant()}|{Start.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";
    public string Rk => SheetParser.Sha(Key)[..32];

    // Changes when the cell text or the end of the range changes.
    public string Hash => SheetParser.Sha(SheetParser.Norm(Text) + "|" + End.ToString("yyyyMMdd", CultureInfo.InvariantCulture))[..16];
}

/// <summary>
/// Reads the tracker layout: a header row of dates (Oct 1, Oct 2, ...), then one row per subject.
/// Every non-empty cell under a date is an event; identical neighbouring cells become one date range.
/// </summary>
public static class SheetParser
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly Regex DateCell = new(@"^\s*([A-Za-z]{3,9})\.?\s+(\d{1,2})\s*$", RegexOptions.Compiled);
    static readonly Regex MonthYearCell = new(@"^\s*([A-Za-z]{3,9})\s+(\d{4})\s*$", RegexOptions.Compiled);
    static readonly string[] Months = { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };

    public static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    public static string Norm(string s) => Regex.Replace(s, @"\s+", " ").Trim().ToLowerInvariant();

    // ---------- CSV ----------
    public static List<List<string>> ParseCsv(string s)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < s.Length && s[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(ch);
            }
            else if (ch == '"') inQuotes = true;
            else if (ch == ',') { row.Add(sb.ToString()); sb.Clear(); }
            else if (ch == '\n' || ch == '\r')
            {
                if (ch == '\r' && i + 1 < s.Length && s[i + 1] == '\n') i++;
                row.Add(sb.ToString());
                sb.Clear();
                rows.Add(row);
                row = new List<string>();
            }
            else sb.Append(ch);
        }
        if (sb.Length > 0 || row.Count > 0) { row.Add(sb.ToString()); rows.Add(row); }
        return rows;
    }

    // ---------- layout ----------
    public static List<SheetItem> Parse(string gid, List<List<string>> rows, DateOnly today)
    {
        var items = new List<SheetItem>();
        List<(int col, DateOnly date)>? cols = null;

        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];

            var header = HeaderCells(row);
            if (header.Count >= 3)
            {
                cols = BuildCols(header, FindYear(rows, r), today);
                continue;
            }
            if (cols is null || row.Count == 0) continue;

            var subject = Regex.Replace(row[0], @"\s+", " ").Trim();
            if (subject.Length == 0) continue; // weekday row, blank row

            var i = 0;
            while (i < cols.Count)
            {
                var text = Cell(row, cols[i].col);
                if (text.Length == 0) { i++; continue; }

                var norm = Norm(text);
                var j = i;
                while (j + 1 < cols.Count && Norm(Cell(row, cols[j + 1].col)) == norm) j++;

                var cellRef = ColName(cols[i].col) + (r + 1) + (j > i ? ":" + ColName(cols[j].col) + (r + 1) : "");
                items.Add(new SheetItem(gid, subject, cols[i].date, cols[j].date, text, cellRef));
                i = j + 1;
            }
        }
        return items;
    }

    static string Cell(List<string> row, int col) => col < row.Count ? row[col].Trim() : "";

    static List<(int col, int month, int day)> HeaderCells(List<string> row)
    {
        var res = new List<(int col, int month, int day)>();
        for (var c = 1; c < row.Count; c++)
        {
            var m = DateCell.Match(row[c]);
            if (!m.Success) continue;
            var month = MonthOf(m.Groups[1].Value);
            if (month == 0) continue;
            res.Add((c, month, int.Parse(m.Groups[2].Value, Inv)));
        }
        return res;
    }

    static List<(int col, DateOnly date)> BuildCols(List<(int col, int month, int day)> cells, int? year, DateOnly today)
    {
        var cols = new List<(int col, DateOnly date)>();
        var y = year ?? InferYear(cells[0].month, today);
        var prevMonth = 0;
        foreach (var (col, month, day) in cells)
        {
            if (prevMonth != 0 && month < prevMonth) y++; // Dec -> Jan
            prevMonth = month;
            if (day < 1 || day > DateTime.DaysInMonth(y, month)) continue;
            cols.Add((col, new DateOnly(y, month, day)));
        }
        return cols;
    }

    // "October 2026" near the header tells us the year.
    static int? FindYear(List<List<string>> rows, int headerRow)
    {
        for (var r = headerRow; r >= Math.Max(0, headerRow - 3); r--)
            foreach (var cell in rows[r])
            {
                var m = MonthYearCell.Match(cell);
                if (m.Success && MonthOf(m.Groups[1].Value) != 0) return int.Parse(m.Groups[2].Value, Inv);
            }
        return null;
    }

    static int InferYear(int month, DateOnly today)
    {
        var best = today.Year;
        var bestDiff = int.MaxValue;
        for (var y = today.Year - 1; y <= today.Year + 1; y++)
        {
            var diff = Math.Abs(new DateOnly(y, month, 15).DayNumber - today.DayNumber);
            if (diff < bestDiff) { bestDiff = diff; best = y; }
        }
        return best;
    }

    static int MonthOf(string word)
    {
        var w = word.ToLowerInvariant();
        if (w.Length < 3) return 0;
        var i = Array.IndexOf(Months, w[..3]);
        if (i < 0) return 0;
        var full = Inv.DateTimeFormat.GetMonthName(i + 1).ToLowerInvariant();
        return w.Length == 3 || full.StartsWith(w, StringComparison.Ordinal) || w == "sept" ? i + 1 : 0;
    }

    static string ColName(int index)
    {
        var s = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
            s = (char)('A' + (n - 1) % 26) + s;
        return s;
    }
}