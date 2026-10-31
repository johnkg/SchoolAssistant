using System.Text.Json;
using Xunit;

namespace SchoolAssistant.Tests;

public class TextUtilTests
{
    [Fact] public void Flatten_collapses_all_whitespace() => Assert.Equal("a b c", TextUtil.Flatten("  a \n\t b\r\n c  "));
    [Fact] public void Truncate_leaves_short_text() => Assert.Equal("abc", TextUtil.Truncate("abc", 3));
    [Fact] public void Truncate_marks_cut_text() => Assert.Equal("ab…", TextUtil.Truncate("abcd", 2));
    [Fact] public void Flat_flattens_then_truncates() => Assert.Equal("a b…", TextUtil.Flat("a\n\nb c d", 3));

    [Fact]
    public void Short_removes_newlines_and_limits_length()
    {
        var text = TextUtil.Short(new Exception("line1\nline2" + new string('x', 300)));
        Assert.DoesNotContain('\n', text);
        Assert.Equal(201, text.Length); // 200 characters plus the ellipsis
    }
}

public class DateTextTests
{
    static readonly DateOnly Oct9 = new(2026, 10, 9);

    [Fact] public void TryDate_reads_iso_dates() { Assert.True(DateText.TryDate("2026-10-09", out var d)); Assert.Equal(Oct9, d); }
    [Theory, InlineData(null), InlineData(""), InlineData("10/09/2026"), InlineData("2026-13-01")]
    public void TryDate_rejects_everything_else(string? text) => Assert.False(DateText.TryDate(text, out _));

    [Fact] public void TryTime_reads_24_hour_times() { Assert.True(DateText.TryTime("14:05", out var t)); Assert.Equal(new TimeOnly(14, 5), t); }
    [Theory, InlineData(null), InlineData("2pm"), InlineData("25:00")]
    public void TryTime_rejects_everything_else(string? text) => Assert.False(DateText.TryTime(text, out _));

    [Fact] public void Iso_and_Compact_format_dates() { Assert.Equal("2026-10-09", DateText.Iso(Oct9)); Assert.Equal("20261009", DateText.Compact(Oct9)); }
    [Fact] public void Day_is_short_and_culture_independent() => Assert.Equal("Fri Oct 9", DateText.Day(Oct9));
    [Fact] public void Time_is_12_hour() => Assert.Equal("2:05 PM", DateText.Time(new TimeOnly(14, 5)));
    [Fact] public void Range_of_one_day_is_just_the_day() => Assert.Equal("Fri Oct 9", DateText.Range(Oct9, Oct9));
    [Fact] public void Range_of_several_days_shows_both_ends() => Assert.Equal("Fri Oct 9 – Mon Oct 12", DateText.Range(Oct9, Oct9.AddDays(3)));
    [Fact] public void Range_with_end_before_start_shows_only_start() => Assert.Equal("Fri Oct 9", DateText.Range(Oct9, Oct9.AddDays(-1)));
}

public class LlmUtilTests
{
    [Fact] public void StripFences_removes_code_fences() => Assert.Equal("{\"a\":1}", LlmUtil.StripFences("```json\n{\"a\":1}\n```"));
    [Fact] public void StripFences_drops_text_before_and_after() => Assert.Equal("{\"a\":1}", LlmUtil.StripFences("Here you go: {\"a\":1} Hope that helps!"));
    [Fact] public void StripFences_keeps_arrays() => Assert.Equal("[1,2]", LlmUtil.StripFences("x [1,2] y"));
    [Fact] public void StripFences_ignores_braces_inside_strings() => Assert.Equal("{\"a\":\"}{\"}", LlmUtil.StripFences("{\"a\":\"}{\"} trailing"));
    [Fact] public void StripFences_handles_escaped_quotes() => Assert.Equal("{\"a\":\"x\\\"}\"}", LlmUtil.StripFences("{\"a\":\"x\\\"}\"} more"));
    [Fact] public void StripFences_keeps_nested_values() => Assert.Equal("{\"a\":{\"b\":[1]}}", LlmUtil.StripFences("{\"a\":{\"b\":[1]}} {\"c\":2}"));
    [Fact] public void StripFences_returns_plain_text_when_there_is_no_json() => Assert.Equal("no json here", LlmUtil.StripFences("  no json here "));
    [Fact] public void StripFences_returns_cut_off_json_for_the_parser_to_reject() => Assert.Equal("{\"a\":[1", LlmUtil.StripFences("{\"a\":[1"));

    [Fact]
    public void ParseJson_parses_the_first_value()
    {
        using var doc = LlmUtil.ParseJson("sure! {\"x\":5}");
        Assert.Equal(5, doc.RootElement.GetProperty("x").GetInt32());
    }

    [Fact] public void ParseJson_throws_JsonException_for_non_json() => Assert.ThrowsAny<JsonException>(() => LlmUtil.ParseJson("nothing"));

    [Fact]
    public void Today_is_the_long_manila_date()
    {
        Assert.Matches(@"^[A-Z][a-z]+day, \d{4}-\d{2}-\d{2}$", LlmUtil.Today());
    }
}

public class ManilaTimeTests
{
    [Fact] public void Offset_is_eight_hours() => Assert.Equal(TimeSpan.FromHours(8), ManilaTime.Offset);
    [Fact] public void Now_uses_the_manila_offset() => Assert.Equal(ManilaTime.Offset, ManilaTime.Now.Offset);
    [Fact] public void Today_matches_now() => Assert.True(Math.Abs(ManilaTime.Today.DayNumber - DateOnly.FromDateTime(ManilaTime.Now.DateTime).DayNumber) <= 1);
}

public class EnvTests : AppTestBase
{
    [Fact] public void Get_returns_the_value() => Assert.Equal("cal-id", Env.Get(Settings.CalendarId));

    [Fact]
    public void Get_throws_naming_the_missing_setting()
    {
        SetEnv(Settings.SheetId, null);
        var ex = Assert.Throws<InvalidOperationException>(() => Env.Get(Settings.SheetId));
        Assert.Contains(Settings.SheetId, ex.Message);
    }

    [Fact] public void GetOr_returns_the_value_when_set() => Assert.Equal("cal-id", Env.GetOr(Settings.CalendarId, "fallback"));

    [Theory, InlineData(null), InlineData("")]
    public void GetOr_falls_back_when_missing_or_empty(string? value)
    {
        SetEnv(Settings.SheetId, value);
        Assert.Equal("fallback", Env.GetOr(Settings.SheetId, "fallback"));
    }

    [Theory, InlineData("true", false, true), InlineData("False", true, false), InlineData("yes", true, true),
     InlineData("yes", false, false), InlineData(null, true, true), InlineData(null, false, false)]
    public void GetFlag_reads_booleans_and_defaults_otherwise(string? value, bool defaultValue, bool expected)
    {
        SetEnv(Settings.DebugReplies, value);
        Assert.Equal(expected, Env.GetFlag(Settings.DebugReplies, defaultValue));
    }

    [Fact] public void TelegramChatId_is_parsed_as_a_number() => Assert.Equal(ChatId, Env.TelegramChatId);
}

public class DbTests : AppTestBase
{
    [Fact]
    public void Table_goes_through_the_factory()
    {
        var table = Db.Table("Anything");
        Assert.Same(Table("Anything"), table);
    }
}
