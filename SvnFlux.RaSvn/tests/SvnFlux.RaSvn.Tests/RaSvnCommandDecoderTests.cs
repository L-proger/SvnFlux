using SvnFlux.RaSvn.Protocol;
using SvnFlux.RaSvn.Wire;

namespace SvnFlux.RaSvn.Tests;

public sealed class RaSvnCommandDecoderTests
{
    [Fact]
    public void DecodesTypedGetFileCommand()
    {
        var command = new SvnWireList([
            new SvnWireWord("get-file"),
            new SvnWireList([
                Text("readme.txt"),
                new SvnWireList([new SvnWireNumber(1)]),
                new SvnWireWord("true"),
                new SvnWireWord("true")
            ])
        ]);

        var result = Assert.IsType<GetFileCommand>(RaSvnCommandDecoder.Decode(command));

        Assert.Equal("readme.txt", result.Path.Value);
        Assert.Equal(1, result.Revision?.Value);
        Assert.True(result.WantProperties);
        Assert.True(result.WantContents);
    }

    [Fact]
    public void RejectsPathTraversalBeforeRepositoryAccess()
    {
        var command = new SvnWireList([
            new SvnWireWord("check-path"),
            new SvnWireList([Text("../secret"), new SvnWireList([])])
        ]);

        Assert.Throws<SvnWireProtocolException>(() => RaSvnCommandDecoder.Decode(command));
    }

    [Fact]
    public void RejectsInvalidBooleanField()
    {
        var command = new SvnWireList([
            new SvnWireWord("get-dir"),
            new SvnWireList([
                Text(string.Empty),
                new SvnWireList([]),
                new SvnWireWord("maybe"),
                new SvnWireWord("true")
            ])
        ]);

        Assert.Throws<SvnWireProtocolException>(() => RaSvnCommandDecoder.Decode(command));
    }

    [Fact]
    public void DecodesUpdateCommand()
    {
        var command = new SvnWireList([
            new SvnWireWord("update"),
            new SvnWireList([
                new SvnWireList([new SvnWireNumber(7)]),
                Text(string.Empty),
                new SvnWireWord("true"),
                new SvnWireWord("infinity"),
                new SvnWireWord("false"),
                new SvnWireWord("false")
            ])
        ]);

        var result = Assert.IsType<UpdateCommand>(RaSvnCommandDecoder.Decode(command));

        Assert.Equal(7, result.Revision?.Value);
        Assert.True(result.Target.IsRoot);
        Assert.Equal("infinity", result.Depth);
    }

    [Fact]
    public void DecodesSetPathReportCommand()
    {
        var command = new SvnWireList([
            new SvnWireWord("set-path"),
            new SvnWireList([
                Text(string.Empty),
                new SvnWireNumber(3),
                new SvnWireWord("false"),
                new SvnWireList([]),
                new SvnWireWord("infinity")
            ])
        ]);

        var result = Assert.IsType<SetPathReportCommand>(RaSvnReportCommandDecoder.Decode(command));

        Assert.True(result.Path.IsRoot);
        Assert.Equal(3, result.Revision.Value);
        Assert.False(result.StartEmpty);
        Assert.Equal("infinity", result.Depth);
    }

    [Fact]
    public void DecodesStatusAndDiffCommands() {
        var status = new SvnWireList([new SvnWireWord("status"), new SvnWireList([Text(string.Empty), new SvnWireWord("true"), new SvnWireList([new SvnWireNumber(4)]), new SvnWireWord("infinity")])]);
        var diff = new SvnWireList([new SvnWireWord("diff"), new SvnWireList([new SvnWireList([new SvnWireNumber(5)]), Text(string.Empty), new SvnWireWord("true"), new SvnWireWord("false"), Text("svn://localhost/repository"), new SvnWireWord("true"), new SvnWireWord("infinity")])]);

        var decodedStatus = Assert.IsType<StatusCommand>(RaSvnCommandDecoder.Decode(status));
        var decodedDiff = Assert.IsType<DiffCommand>(RaSvnCommandDecoder.Decode(diff));

        Assert.Equal(4, decodedStatus.Revision?.Value);
        Assert.Equal("infinity", decodedStatus.Depth);
        Assert.Equal(5, decodedDiff.Revision?.Value);
        Assert.True(decodedDiff.TextDeltas);
        Assert.Equal("svn://localhost/repository", decodedDiff.VersusUrl.AbsoluteUri.TrimEnd('/'));
    }

    [Fact]
    public void DecodesOfficialClientZeroLengthPropertyAsDelete() {
        var command = new SvnWireList([new SvnWireWord("change-file-prop"), new SvnWireList([Text("f1"), Text("custom:value"), new SvnWireList([Text(string.Empty)])])]);

        var decoded = Assert.IsType<ChangeFilePropertyEditorCommand>(RaSvnEditorCommandDecoder.Decode(command));

        Assert.Null(decoded.Value);
    }

    [Fact]
    public void DecodesFileRevisionAndLocationCommands() {
        var fileRevisions = new SvnWireList([new SvnWireWord("get-file-revs"), new SvnWireList([Text("renamed.txt"), new SvnWireList([new SvnWireNumber(0)]), new SvnWireList([new SvnWireNumber(4)]), new SvnWireWord("false")])]);
        var locations = new SvnWireList([new SvnWireWord("get-locations"), new SvnWireList([Text("renamed.txt"), new SvnWireNumber(4), new SvnWireList([new SvnWireNumber(1), new SvnWireNumber(2)])])]);

        var decodedRevisions = Assert.IsType<GetFileRevisionsCommand>(RaSvnCommandDecoder.Decode(fileRevisions));
        var decodedLocations = Assert.IsType<GetLocationsCommand>(RaSvnCommandDecoder.Decode(locations));

        Assert.Equal(0, decodedRevisions.StartRevision?.Value);
        Assert.Equal(4, decodedRevisions.EndRevision?.Value);
        Assert.Equal([1L, 2L], decodedLocations.Revisions.Select(revision => revision.Value));
    }

    [Fact]
    public void DecodesLocationSegmentsCommand() {
        var command = new SvnWireList([new SvnWireWord("get-location-segments"), new SvnWireList([Text("tree.txt"), new SvnWireList([new SvnWireNumber(6)]), new SvnWireList([new SvnWireNumber(6)]), new SvnWireList([new SvnWireNumber(0)])])]);

        var decoded = Assert.IsType<GetLocationSegmentsCommand>(RaSvnCommandDecoder.Decode(command));

        Assert.Equal(6, decoded.PegRevision?.Value);
        Assert.Equal(6, decoded.StartRevision?.Value);
        Assert.Equal(0, decoded.EndRevision?.Value);
    }

    [Fact]
    public void DecodesSwitchAtomicRevpropAndLockMany() {
        var switchCommand = new SvnWireList([new SvnWireWord("switch"), new SvnWireList([new SvnWireList([new SvnWireNumber(7)]), Text(string.Empty), new SvnWireWord("true"), Text("svn://localhost/repository/branch"), new SvnWireWord("infinity"), new SvnWireWord("false"), new SvnWireWord("false")])]);
        var revprop = new SvnWireList([new SvnWireWord("change-rev-prop2"), new SvnWireList([new SvnWireNumber(1), Text("svn:log"), new SvnWireList([Text("new")]), new SvnWireList([new SvnWireWord("false"), Text("old")])])]);
        var locks = new SvnWireList([new SvnWireWord("lock-many"), new SvnWireList([new SvnWireList([Text("comment")]), new SvnWireWord("false"), new SvnWireList([new SvnWireList([Text("file.txt"), new SvnWireList([new SvnWireNumber(7)])])])])]);

        Assert.Equal("branch", Assert.IsType<SwitchCommand>(RaSvnCommandDecoder.Decode(switchCommand)).Url.Segments[^1].Trim('/'));
        Assert.False(Assert.IsType<ChangeRevisionPropertyCommand>(RaSvnCommandDecoder.Decode(revprop)).IgnoreExpectedValue);
        Assert.Equal(7, Assert.Single(Assert.IsType<LockManyCommand>(RaSvnCommandDecoder.Decode(locks)).Targets).Revision?.Value);
    }

    private static SvnWireString Text(string value) => new(System.Text.Encoding.UTF8.GetBytes(value));
}
