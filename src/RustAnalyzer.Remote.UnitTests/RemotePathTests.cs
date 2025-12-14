using FluentAssertions;
using Xunit;

namespace KS.RustAnalyzer.Remote.UnitTests;

public class RemotePathTests
{
    [Fact]
    public void Constructor_NullPath_ThrowsArgumentNullException()
    {
        var act = () => new RemotePath(null, TargetKind.Wsl);

        act.Should().Throw<System.ArgumentNullException>()
            .WithParameterName("path");
    }

    [Fact]
    public void Constructor_LocalPath_PreservesBackslashes()
    {
        var path = new RemotePath(@"C:\Users\test", TargetKind.Local);

        ((string)path).Should().Be(@"C:\Users\test");
    }

    [Fact]
    public void Constructor_WslPath_ConvertsBackslashesToForwardSlashes()
    {
        var path = new RemotePath(@"home\user\test", TargetKind.Wsl);

        ((string)path).Should().Be("home/user/test");
    }

    [Fact]
    public void Constructor_WslPath_PreservesForwardSlashes()
    {
        var path = new RemotePath("/home/user/test", TargetKind.Wsl);

        ((string)path).Should().Be("/home/user/test");
    }

    [Fact]
    public void ImplicitConversionToString_ReturnsPath()
    {
        var path = new RemotePath("/home/user/test", TargetKind.Wsl);
        string result = path;

        result.Should().Be("/home/user/test");
    }

    [Theory]
    [InlineData("/home/user/test", "subdir", "/home/user/test/subdir")]
    [InlineData("/home/user", "file.rs", "/home/user/file.rs")]
    [InlineData("/home/user/", "/subdir", "/home/user/subdir")]
    [InlineData("/", "home", "/home")]
    public void Combine_AppendsSegment(string basePath, string segment, string expected)
    {
        var path = new RemotePath(basePath, TargetKind.Wsl);
        var result = path.Combine(segment);

        ((string)result).Should().Be(expected);
        result.Kind.Should().Be(TargetKind.Wsl);
    }

    [Theory]
    [InlineData("/home/user/test.rs", "test.rs")]
    [InlineData("/home/user/", "")]
    [InlineData("/home/user", "user")]
    [InlineData("/", "/")]
    [InlineData("file.rs", "file.rs")]
    public void GetFileName_ReturnsFileName(string path, string expected)
    {
        var remotePath = new RemotePath(path, TargetKind.Wsl);

        remotePath.GetFileName().Should().Be(expected);
    }

    [Theory]
    [InlineData("/home/user/test.rs", "/home/user")]
    [InlineData("/home/user", "/home")]
    [InlineData("/home", "/")]
    [InlineData("/", "/")]
    public void GetDirectoryName_ReturnsDirectory(string path, string expected)
    {
        var remotePath = new RemotePath(path, TargetKind.Wsl);

        ((string)remotePath.GetDirectoryName()).Should().Be(expected);
    }

    [Theory]
    [InlineData("/home/user/test.rs", ".rs")]
    [InlineData("/home/user/test.tar.gz", ".gz")]
    [InlineData("/home/user/test", "")]
    [InlineData("/home/user/.hidden", ".hidden")]
    public void GetExtension_ReturnsExtension(string path, string expected)
    {
        var remotePath = new RemotePath(path, TargetKind.Wsl);

        remotePath.GetExtension().Should().Be(expected);
    }

    [Theory]
    [InlineData("/home/user/test", "/home", true)]
    [InlineData("/home/user/test", "/home/user", true)]
    [InlineData("/home/user/test", "/other", false)]
    [InlineData("/home/user/test", "home", false)]
    public void StartsWith_ChecksPrefix(string path, string prefix, bool expected)
    {
        var remotePath = new RemotePath(path, TargetKind.Wsl);

        remotePath.StartsWith(prefix).Should().Be(expected);
    }

    [Fact]
    public void Equals_SamePathsAndKind_ReturnsTrue()
    {
        var path1 = new RemotePath("/home/user", TargetKind.Wsl);
        var path2 = new RemotePath("/home/user", TargetKind.Wsl);

        path1.Equals(path2).Should().BeTrue();
        (path1 == path2).Should().BeTrue();
        (path1 != path2).Should().BeFalse();
    }

    [Fact]
    public void Equals_DifferentKinds_ReturnsFalse()
    {
        var path1 = new RemotePath("/home/user", TargetKind.Wsl);
        var path2 = new RemotePath("/home/user", TargetKind.Ssh);

        path1.Equals(path2).Should().BeFalse();
    }

    [Fact]
    public void Equals_WslPaths_CaseSensitive()
    {
        var path1 = new RemotePath("/home/User", TargetKind.Wsl);
        var path2 = new RemotePath("/home/user", TargetKind.Wsl);

        path1.Equals(path2).Should().BeFalse();
    }

    [Fact]
    public void Equals_LocalPaths_CaseInsensitive()
    {
        var path1 = new RemotePath(@"C:\Users\Test", TargetKind.Local);
        var path2 = new RemotePath(@"C:\Users\test", TargetKind.Local);

        path1.Equals(path2).Should().BeTrue();
    }

    [Fact]
    public void IsEmpty_EmptyPath_ReturnsTrue()
    {
        var path = new RemotePath(string.Empty, TargetKind.Wsl);

        path.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void IsEmpty_NonEmptyPath_ReturnsFalse()
    {
        var path = new RemotePath("/home", TargetKind.Wsl);

        path.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void Length_ReturnsPathLength()
    {
        var path = new RemotePath("/home/user", TargetKind.Wsl);

        path.Length.Should().Be("/home/user".Length);
    }

    [Fact]
    public void ToString_ReturnsPath()
    {
        var path = new RemotePath("/home/user", TargetKind.Wsl);

        path.ToString().Should().Be("/home/user");
    }
}

