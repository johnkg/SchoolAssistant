using Xunit;

namespace SchoolAssistant.Tests;

public class ParseCsvTests
{
    [Fact] public void Splits_rows_and_cells() => Assert.Equal("a,b|c,d", string.Join("|", SheetParser.ParseCsv("a,b\nc,d\n").Select(r => string.Join(",", r))));
    [Fact] public void Handles_windows_line_endings() => Assert.Equal(2, SheetParser.ParseCsv("a\r\nb\r\n").Count);
    [Fact] public void Keeps_the_last_row_without_a_final_newline() => Assert.Equal(new[] { "c", "d" }, SheetParser.ParseCsv("a,b\nc,d")[1]);
    [Fact] public void Quoted_cells_may_contain_commas_and_line_breaks() => Assert.Equal("x,y\nz", SheetParser.ParseCsv("\"x,y\nz\",b\n")[0][0]);
    [Fact] public void Doubled_quotes_are_one_quote() => Assert.Equal("say \"hi\"", SheetParser.ParseCsv("\"say \"\"hi\"\"\"\n")[0][0]);
    [Fact] public void Empty_input_has_no_rows() => Assert.Empty(SheetParser.ParseCsv(""));
    [Fact] public void A_blank_line_is_a_row_with_one_empty_cell() => Assert.Equal(new[] { "" }, SheetParser.ParseCsv("\n")[0]);
    [Fact] public void A_lone_carriage_return_ends_a_row() => Assert.Equal(2, SheetParser.ParseCsv("a\rb\r").Count);
}

public class SheetParserTests
{
    static readonly DateOnly Today = new(2026, 10, 31);

    static List<SheetItem> Parse(string csv, DateOnly? today = null) =>
        SheetParser.Parse("123", SheetParser.ParseCsv(csv), today ?? Today);

    [Fact]
    public void Reads_items_under_a_dated_header_and_merges_identical_neighbours()
    {
        var items = Parse(
            ",October 2026,,,,\n" +
            "Subject,Oct 12,Oct 13,Oct 14,Oct 15\n" +
            ",Mon,Tue,Wed,Thu\n" +
            "MATH,WW 1,ww   1,,Quiz\n");

        Assert.Equal(2, items.Count);
        Assert.Equal(("MATH", new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 13), "WW 1", "B4:C4"),
            (items[0].Subject, items[0].Start, items[0].End, items[0].Text, items[0].CellRef));
        Assert.Equal(("Quiz", new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 15), "E4"),
            (items[1].Text, items[1].Start, items[1].End, items[1].CellRef));
    }

    [Fact]
    public void The_year_comes_from_a_month_year_cell_near_the_header()
    {
        var items = Parse("November 2027\nSubject,Nov 3,Nov 4,Nov 5\nART,Draw,,\n");
        Assert.Equal(new DateOnly(2027, 11, 3), items.Single().Start);
    }

    [Fact]
    public void Without_a_year_the_nearest_one_is_used_and_december_rolls_into_january()
    {
        var items = Parse("Subject,Dec 30,Dec 31,Jan 1\nART,a,b,c\n", new DateOnly(2026, 12, 20));
        Assert.Equal(new[] { new DateOnly(2026, 12, 30), new DateOnly(2026, 12, 31), new DateOnly(2027, 1, 1) }, items.Select(i => i.Start));
    }

    [Fact]
    public void Without_a_year_a_month_just_after_new_year_belongs_to_next_year()
    {
        var items = Parse("Subject,Jan 5,Jan 6,Jan 7\nART,a,b,c\n", new DateOnly(2026, 12, 20));
        Assert.Equal(2027, items[0].Start.Year);
    }

    [Fact]
    public void Understands_month_spellings_and_skips_bad_dates()
    {
        var items = Parse("Subject,Sept 3,September 4,Foo 5,Feb 30,Oct. 6,Ma 7,Mayo 8\nA,a,b,c,d,e,f,g\n");
        Assert.Equal(new[] { 9, 9, 10 }, items.Select(i => i.Start.Month));
        Assert.Equal(new[] { 3, 4, 6 }, items.Select(i => i.Start.Day));
    }

    [Fact] public void Needs_at_least_three_date_cells_for_a_header() => Assert.Empty(Parse("Subject,Oct 1,Oct 2\nMATH,a,b\n"));
    [Fact] public void Rows_before_a_header_are_ignored() => Assert.Empty(Parse("MATH,a,b\n"));
    [Fact] public void Rows_without_a_subject_are_ignored() => Assert.Empty(Parse("S,Oct 1,Oct 2,Oct 3\n,x,y,z\n"));

    [Fact]
    public void A_new_header_row_starts_a_new_block_and_short_rows_are_fine()
    {
        var items = Parse("S,Oct 1,Oct 2,Oct 3\nMATH,a\nS,Nov 1,Nov 2,Nov 3\nSCI,,,z\n");
        Assert.Equal(new[] { "MATH", "SCI" }, items.Select(i => i.Subject));
        Assert.Equal(new DateOnly(2026, 11, 3), items[1].Start);
    }

    [Fact]
    public void An_empty_row_in_the_list_is_skipped()
    {
        var rows = new List<List<string>>
        {
            new() { "S", "Oct 1", "Oct 2", "Oct 3" },
            new(),
            new() { "MATH", "x" }
        };
        Assert.Single(SheetParser.Parse("1", rows, Today));
    }

    [Fact]
    public void Column_letters_continue_after_Z()
    {
        var header = "S," + string.Join(",", Enumerable.Range(1, 26).Select(d => $"Oct {d}"));
        var row = "MATH," + string.Join(",", Enumerable.Repeat("", 25)) + ",last";
        Assert.Equal("AA2", Parse(header + "\n" + row + "\n").Single().CellRef);
    }

    [Fact] public void Norm_flattens_and_lowercases() => Assert.Equal("a b", SheetParser.Norm("  A\n B "));
    [Fact] public void Sha_is_stable_hex_of_the_requested_length() { Assert.Equal(8, SheetParser.Sha("x", 8).Length); Assert.Equal(SheetParser.Sha("x", 8), SheetParser.Sha("x", 8)); Assert.NotEqual(SheetParser.Sha("x", 8), SheetParser.Sha("y", 8)); }
}

public class SheetItemTests
{
    static SheetItem Item(string text = "WW 1", DateOnly? end = null) =>
        new("12", "Math", new DateOnly(2026, 10, 12), end ?? new DateOnly(2026, 10, 12), text, "B4");

    [Fact] public void Key_is_tab_subject_and_first_day() => Assert.Equal("12|MATH|20261012", Item().Key);
    [Fact] public void Rk_is_a_fixed_length_hash_of_the_key() => Assert.Equal(32, Item().Rk.Length);
    [Fact] public void Hash_ignores_case_and_spacing_of_the_text() => Assert.Equal(Item("WW 1").Hash, Item(" ww\n1 ").Hash);
    [Fact] public void Hash_changes_with_the_text() => Assert.NotEqual(Item("WW 1").Hash, Item("WW 2").Hash);
    [Fact] public void Hash_changes_with_the_end_date() => Assert.NotEqual(Item().Hash, Item(end: new DateOnly(2026, 10, 14)).Hash);
    [Fact] public void Rk_does_not_change_with_the_text() => Assert.Equal(Item("a").Rk, Item("b").Rk);

    [Fact]
    public void TryParseKey_reads_subject_and_start()
    {
        Assert.True(SheetItem.TryParseKey("12|MATH|20261012", out var subject, out var start));
        Assert.Equal(("MATH", new DateOnly(2026, 10, 12)), (subject, start));
    }

    [Fact] public void TryParseKey_keeps_separators_inside_the_subject() { Assert.True(SheetItem.TryParseKey("12|A|B|20261012", out var subject, out _)); Assert.Equal("A|B", subject); }

    [Theory, InlineData(null), InlineData("nobars"), InlineData("one|20261012"), InlineData("12|MATH|notadate")]
    public void TryParseKey_rejects_bad_keys(string? key) => Assert.False(SheetItem.TryParseKey(key, out _, out _));
}
