namespace SvnFlux.Core.Tests;

public sealed class SvnRepositoryPathTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("file.txt", "file.txt")]
    [InlineData("/directory/file.txt", "directory/file.txt")]
    [InlineData("directory/file.txt/", "directory/file.txt")]
    [InlineData("Данные/Файл.txt", "Данные/Файл.txt")]
    public void NormalizesCanonicalRepositoryPaths(string input, string expected)
    {
        Assert.Equal(expected, new SvnRepositoryPath(input).Value);
    }

    [Theory]
    [InlineData("//")]
    [InlineData("directory//file")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("directory/../file")]
    [InlineData("directory/./file")]
    [InlineData("directory\\file")]
    [InlineData("file\0name")]
    public void RejectsAmbiguousOrUnsafePaths(string input)
    {
        Assert.Throws<ArgumentException>(() => new SvnRepositoryPath(input));
    }

    [Fact]
    public void AppendCombinesCanonicalPaths()
    {
        var parent = new SvnRepositoryPath("trunk");
        var child = new SvnRepositoryPath("source/file.cs");

        Assert.Equal("trunk/source/file.cs", parent.Append(child).Value);
    }
}
