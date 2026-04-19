using SharpClaw.API.Helpers;

namespace SharpClaw.API.UnitTests;

public class StringReplacerTests
{
    private const string LfContent =
        "  123\n" +
        "  456\n" +
        "  789\n" +
        "  456\n" +
        "  123\n" +
        "  456";

    private const string CrLfContent =
        "  123\r\n" +
        "  456\r\n" +
        "  789\r\n" +
        "  456\r\n" +
        "  123\r\n" +
        "  456";

    [Fact]
    public void NoMatch_ReturnsError()
    {
        var (file, error) = StringReplacer.Replace(CrLfContent,
            "not-matching-string", "  abc\r\n  def", false);

        Assert.Equal(StringReplacer.Error.OldStringNotFound, error);
        Assert.Null(file);
    }

    [Fact]
    public void MultipleMatches_ReturnsError()
    {
        var (file, error) = StringReplacer.Replace(CrLfContent,
            "123", "  abc\r\n  def", false);

        Assert.Equal(StringReplacer.Error.MultipleMatchesFound, error);
        Assert.Null(file);
    }

    [Fact]
    public void PerfectMatch_SingleMatch_Lf()
    {
        var (file, error) = StringReplacer.Replace(LfContent,
            "  456\n  789", "  abc\n  def", false);

        Assert.Null(error);
        Assert.Equal(
            "  123\n" +
            "  abc\n" +
            "  def\n" +
            "  456\n" +
            "  123\n" +
            "  456", file);
    }

    [Fact]
    public void PerfectMatch_SingleMatch_CrLf()
    {
        var (file, error) = StringReplacer.Replace(CrLfContent,
            "  456\r\n  789", "  abc\r\n  def", false);

        Assert.Null(error);
        Assert.Equal(
            "  123\r\n" +
            "  abc\r\n" +
            "  def\r\n" +
            "  456\r\n" +
            "  123\r\n" +
            "  456", file);
    }

    [Fact]
    public void PerfectMatch_MultipleMatch_Lf()
    {
        var (file, error) = StringReplacer.Replace(LfContent,
            "  123\n  456", "  abc\n  def", true);

        Assert.Null(error);
        Assert.Equal(
            "  abc\n" +
            "  def\n" +
            "  789\n" +
            "  456\n" +
            "  abc\n" +
            "  def", file);
    }

    [Fact]
    public void PerfectMatch_MultipleMatch_CrLf()
    {
        var (file, error) = StringReplacer.Replace(CrLfContent,
            "  123\r\n  456", "  abc\r\n  def", true);

        Assert.Null(error);
        Assert.Equal(
            "  abc\r\n" +
            "  def\r\n" +
            "  789\r\n" +
            "  456\r\n" +
            "  abc\r\n" +
            "  def", file);
    }

    [Fact]
    public void LineEndingMismatch_SingleMatch_Lf()
    {
        var (file, error) = StringReplacer.Replace(LfContent,
            "  456\r\n  789", "  abc\r\n  def", false);

        Assert.Null(error);
        Assert.Equal(
            "  123\n" +
            "  abc\n" +
            "  def\n" +
            "  456\n" +
            "  123\n" +
            "  456", file);
    }

    [Fact]
    public void LineEndingMismatch_SingleMatch_CrLf()
    {
        var (file, error) = StringReplacer.Replace(CrLfContent,
            "  456\n  789", "  abc\n  def", false);

        Assert.Null(error);
        Assert.Equal(
            "  123\r\n" +
            "  abc\r\n" +
            "  def\r\n" +
            "  456\r\n" +
            "  123\r\n" +
            "  456", file);
    }

    [Fact]
    public void LineEndingMismatch_MultipleMatch_Lf()
    {
        var (file, error) = StringReplacer.Replace(LfContent,
            "  123\r\n  456", "  abc\r\n  def", true);

        Assert.Null(error);
        Assert.Equal(
            "  abc\n" +
            "  def\n" +
            "  789\n" +
            "  456\n" +
            "  abc\n" +
            "  def", file);
    }

    [Fact]
    public void LineEndingMismatch_MultipleMatch_CrLf()
    {
        var (file, error) = StringReplacer.Replace(CrLfContent,
            "  123\n  456", "  abc\n  def", true);

        Assert.Null(error);
        Assert.Equal(
            "  abc\r\n" +
            "  def\r\n" +
            "  789\r\n" +
            "  456\r\n" +
            "  abc\r\n" +
            "  def", file);
    }
}