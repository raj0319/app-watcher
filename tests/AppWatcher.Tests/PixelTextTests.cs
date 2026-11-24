using AppWatcher.Core.Screens;
using Xunit;

namespace AppWatcher.Tests;

/// <summary>A box read from its picture while the program is too busy to answer a text read (synthetic values only).</summary>
public class PixelTextTests
{
    [Theory]
    [InlineData("100", 3, "100")]
    [InlineData(" 100 ", 2, "100")]   // two zeros in one 200 ms key read count once
    [InlineData("AB12", 2, "AB12")]
    public void A_short_word_the_keys_could_write_is_kept(string picture, int keys, string expected)
        => Assert.Equal(expected, FieldReads.PixelTextFits(picture, keys));

    [Theory]
    [InlineData("100", 0)]                       // no key went down: the program's text
    [InlineData("Some Long Product Name", 3)]  // words: the program's
    [InlineData("1000000000", 2)]                // far longer than the keys
    [InlineData("", 3)]
    [InlineData(null, 3)]
    public void Anything_else_is_dropped(string? picture, int keys)
        => Assert.Null(FieldReads.PixelTextFits(picture, keys));

    [Theory]
    [InlineData("100", "100EX", true)]               // the last two keys drawn after the first read
    [InlineData("100", "100", false)]                // nothing new
    [InlineData("100", "Some Product Name", false)]  // the program's answer
    [InlineData("100", "100ABCDEFG", false)]         // far more than a few keys
    [InlineData("", "100", false)]
    public void A_second_read_counts_only_the_keys_not_yet_drawn(string earlier, string later, bool expected)
        => Assert.Equal(expected, FieldReads.ExtendsByKeys(earlier, later));

    [Theory]
    [InlineData(0, 300, 305, true)]      // keys, then Tab, then the next field filled 5 ms later: the program's
    [InlineData(0, 300, 1400, true)]
    [InlineData(0, 300, 2500, false)]    // long after the Tab: not decided by it
    [InlineData(500, 300, 520, false)]   // a key after the Tab: typed in this field
    [InlineData(300, 300, 700, true)]    // keys and Tab seen in the same read (typed in one burst): the Tab ends them
    [InlineData(0, -1, 300, false)]      // no Tab at all (Enter, or nothing): not decided by it
    public void A_value_that_comes_with_Tab_is_the_programs(int typingMs, int tabMs, int valueMs, bool expected)
    {
        var t0 = new DateTime(2026, 1, 1, 9, 0, 0);
        var tab = tabMs < 0 ? DateTime.MinValue : t0.AddMilliseconds(tabMs);
        Assert.Equal(expected, FieldReads.FilledOnTabArrival(t0.AddMilliseconds(typingMs), tab, t0.AddMilliseconds(valueMs)));
    }

    [Fact]
    public void A_WPF_cell_placeholder_gives_way_to_the_editor_inside_it()
    {
        Assert.True(FieldReads.IsItemPlaceholder("Item: SampleWpf.OrderLine, Column Display Index: 1"));
        Assert.False(FieldReads.IsItemPlaceholder("Item 5"));
        var rows = new List<AppWatcher.Core.Screens.ControlRow>
        {
            new() { Depth = 0, Type = "DataGrid", Name = "Lines", Position = "0,0 400x100" },
            new() { Depth = 1, Type = "HeaderItem", Name = "Quantity", Position = "0,0 100x20" },
            new() { Depth = 1, Type = "DataItem", Name = "SampleWpf.OrderLine", Position = "0,20 400x20" },
            new() { Depth = 2, Type = "Custom", Name = "Item: SampleWpf.OrderLine, Column Display Index: 0", Value = "Item: SampleWpf.OrderLine, Column Display Index: 0", Position = "0,20 100x20" },
            new() { Depth = 3, Type = "Edit", Name = "", Value = "5", Position = "2,22 96x16" },
        };
        var cell = Assert.Single(FieldReads.FromRows(rows), f => f.Label == "Quantity");
        Assert.Equal("5", cell.Value);
        rows.Insert(2, new() { Depth = 1, Type = "HeaderItem", Name = "Unit price", Position = "100,0 100x20" });
        Assert.Equal("Quantity", FieldReads.ColumnOfPlaceholder("Item: SampleWpf.OrderLine, Column Display Index: 0", "2,22 96x16", rows));
        Assert.Equal("Unit price", FieldReads.ColumnOfPlaceholder("Item: SampleWpf.OrderLine, Column Display Index: 1", "102,22 96x16", rows));
        Assert.Equal("", FieldReads.ColumnOfPlaceholder("Item: SampleWpf.OrderLine, Column Display Index: 7", "2,22 96x16", rows));
        var lines = FieldReads.GridLines(rows, 0, 10);
        Assert.Equal("5", lines[1]); // the row line shows the editor's value, not the placeholder
    }

    [Theory]
    [InlineData("客户名称", "aaaa")]   // letters without case (Chinese)
    [InlineData("مرحبا", "aaaaa")] // Arabic
    [InlineData("Ⅻ½", "##")]   // number signs that are not digits
    public void A_shape_hides_letters_of_every_script(string value, string expected)
        => Assert.Equal(expected, AppWatcher.Core.Masking.Shape(value));

    [Theory]
    [InlineData("1OO", "100-WIDGET", "100")]   // O read for 0
    [InlineData("I0S", "105X", "105")]          // I for 1, S for 5
    [InlineData("100", "100ABC", "100")]
    [InlineData("1OO", "200-WIDGET", "1OO")]    // the program's value does not extend it: the picture stays
    [InlineData("1OO", "10", "1OO")]            // shorter value
    [InlineData("1OO", null, "1OO")]
    public void The_later_value_settles_look_alike_characters(string picture, string? later, string expected)
        => Assert.Equal(expected, FieldReads.ReconcilePixelText(picture, later));
}
