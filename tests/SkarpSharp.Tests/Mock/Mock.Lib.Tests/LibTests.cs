// The mock library's tests (TUnit), run under net10.0 only; each pattern file says what they leave untested.
using Mock.Lib;
using Mock.Shared;

namespace Mock.Lib.Tests;

public class LibTests
{
    [Test]
    public async Task Grades_cover_every_band()
    {
        await Assert.That(Grades.Letter(95)).IsEqualTo("A");
        await Assert.That(Grades.Letter(85)).IsEqualTo("B");
        await Assert.That(Grades.Letter(75)).IsEqualTo("C");
        await Assert.That(Grades.Letter(65)).IsEqualTo("D");
        await Assert.That(Grades.Letter(5)).IsEqualTo("F");
    }

    [Test]
    public async Task Tokens_are_classified_inside_the_lambda()
    {
        var kinds = Tokenizer.Kinds(["1", "a", "(", "+", ""]);
        await Assert.That(string.Join(",", kinds)).IsEqualTo("number,word,paren,symbol,empty");
    }

    [Test]
    public async Task Async_count()
    {
        await Assert.That(await Feeds.CountAsync([1, 2, 6, -1, 50], 10)).IsEqualTo(4);
    }

    [Test]
    public async Task Local_function_filters_segments()
    {
        await Assert.That(Paths.Normalize("a/./b/~c/d.tmp/e")).IsEqualTo("a/b/e");
    }

    [Test]
    public async Task Customer_and_point()
    {
        var c = new Customer { Name = "Ada", Orders = 2 };
        await Assert.That(c.Display).IsEqualTo("Ada (2)");
        await Assert.That(new Point(1, 2).X).IsEqualTo(1);
    }

    [Test]
    public async Task Status_text()
    {
        await Assert.That(StatusText.Describe(200)).IsEqualTo("ok");
        await Assert.That(StatusText.Describe(503)).IsEqualTo("server error");
    }

    [Test]
    public async Task Order_total()
    {
        var o = new Order();
        o.Lines.Add(2);
        o.Lines.Add(3);
        await Assert.That(o.Total()).IsEqualTo(5m);
    }

    [Test]
    public async Task Feature_flags_inside_if_block()
    {
        await Assert.That(Features.Flags(20)).IsEqualTo(1 | 2 | 8 | 16);
    }

    [Test]
    public async Task Lambda_created_not_invoked()
    {
        await Assert.That(LazyMath.Prepare(2)).IsEqualTo(3);
    }

    [Test]
    public async Task Linked_file_lib_copy()
    {
        await Assert.That(Linked.Origin(5)).IsEqualTo("lib positive");
    }

#if NET8_0
    // Compiled only for net8.0, which this project does not target: Compat.Legacy reads "untested" in a net10.0 report.
    [Test]
    public async Task Legacy_padding()
    {
        await Assert.That(Compat.Legacy(" ab ", true, 4)).IsEqualTo("AB  ");
    }
#endif
}
