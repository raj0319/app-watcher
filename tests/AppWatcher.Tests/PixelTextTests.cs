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
    [InlineData("客户名称", "aaaa")]   // letters without case
    [InlineData("مرحبا", "aaaaa")]
    [InlineData("Ⅻ½", "##")]           // number signs that are not digits
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
