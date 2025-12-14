using System;
using FluentAssertions;
using KS.RustAnalyzer.TestAdapter.Common;
using Xunit;

namespace KS.RustAnalyzer.Remote.UnitTests;

public class WslPathMapperTests
{
    private readonly WslPathMapper _mapper = new WslPathMapper("Ubuntu");

    [Fact]
    public void Constructor_NullDistroName_ThrowsArgumentNullException()
    {
        var act = () => new WslPathMapper(null);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("distroName");
    }

    [Fact]
    public void Kind_ReturnsWsl()
    {
        _mapper.Kind.Should().Be(TargetKind.Wsl);
    }

    [Fact]
    public void DistroName_ReturnsDistroName()
    {
        _mapper.DistroName.Should().Be("Ubuntu");
    }

    [Theory]
    [InlineData(@"\\wsl$\Ubuntu\home\user\proj", "/home/user/proj")]
    [InlineData(@"\\wsl$\Ubuntu\home\user\proj\src\main.rs", "/home/user/proj/src/main.rs")]
    [InlineData(@"\\wsl$\Ubuntu\", "/")]
    [InlineData(@"\\wsl$\Ubuntu", "/")]
    public void MapToRemote_ConvertsUncToLinux(string input, string expected)
    {
        var result = _mapper.MapToRemote((PathEx)input);

        ((string)result).Should().Be(expected);
        result.Kind.Should().Be(TargetKind.Wsl);
    }

    [Theory]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\user\proj", "/home/user/proj")]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\user\proj\src\main.rs", "/home/user/proj/src/main.rs")]
    public void MapToRemote_ConvertsWslLocalhostUncToLinux(string input, string expected)
    {
        var result = _mapper.MapToRemote((PathEx)input);

        ((string)result).Should().Be(expected);
    }

    [Fact]
    public void MapToRemote_NonWslPath_ThrowsArgumentException()
    {
        var act = () => _mapper.MapToRemote((PathEx)@"C:\Users\test");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*is not a WSL path*");
    }

    [Fact]
    public void MapToRemote_WrongDistro_ThrowsArgumentException()
    {
        var act = () => _mapper.MapToRemote((PathEx)@"\\wsl$\Debian\home\user");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*is not a WSL path for distro 'Ubuntu'*");
    }

    [Theory]
    [InlineData("/home/user/proj", @"\\wsl$\Ubuntu\home\user\proj")]
    [InlineData("/home/user/proj/src/main.rs", @"\\wsl$\Ubuntu\home\user\proj\src\main.rs")]
    [InlineData("/", @"\\wsl$\Ubuntu\")]
    public void MapToLocal_ConvertsLinuxToUnc(string input, string expected)
    {
        var remotePath = new RemotePath(input, TargetKind.Wsl);
        var result = _mapper.MapToLocal(remotePath);

        ((string)result).Should().Be(expected);
    }

    [Fact]
    public void MapToLocal_AddsMissingLeadingSlash()
    {
        var remotePath = new RemotePath("home/user", TargetKind.Wsl);
        var result = _mapper.MapToLocal(remotePath);

        ((string)result).Should().Be(@"\\wsl$\Ubuntu\home\user");
    }

    [Fact]
    public void MapToLocal_WrongKind_ThrowsArgumentException()
    {
        var remotePath = new RemotePath("/home/user", TargetKind.Ssh);
        var act = () => _mapper.MapToLocal(remotePath);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Expected WSL path*");
    }

    [Theory]
    [InlineData(@"\\wsl$\Ubuntu\home\user", true)]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\user", true)]
    [InlineData("/home/user", true)]
    [InlineData(@"C:\Users\test", false)]
    [InlineData(@"\\server\share", false)]
    [InlineData(@"\\wsl$\Debian\home\user", false)]
    public void IsPathForTarget_DetectsWslPaths(string path, bool expected)
    {
        _mapper.IsPathForTarget(path).Should().Be(expected);
    }

    [Theory]
    [InlineData(@"\\wsl$\Ubuntu\home\user", "Ubuntu")]
    [InlineData(@"\\wsl$\Ubuntu-20.04\home\user", "Ubuntu-20.04")]
    [InlineData(@"\\wsl.localhost\Debian\opt", "Debian")]
    [InlineData(@"\\wsl$\Arch", "Arch")]
    public void TryGetDistroName_ValidWslPath_ReturnsTrue(string path, string expectedDistro)
    {
        var result = WslPathMapper.TryGetDistroName(path, out var distroName);

        result.Should().BeTrue();
        distroName.Should().Be(expectedDistro);
    }

    [Theory]
    [InlineData(@"C:\Users\test")]
    [InlineData(@"\\server\share")]
    [InlineData("/home/user")]
    [InlineData("")]
    [InlineData(null)]
    public void TryGetDistroName_InvalidPath_ReturnsFalse(string path)
    {
        var result = WslPathMapper.TryGetDistroName(path, out var distroName);

        result.Should().BeFalse();
        distroName.Should().BeNull();
    }

    [Theory]
    [InlineData("file:///home/user/proj/src/main.rs")]
    public void MapUriToLocal_LinuxUri_ConvertsToUncUri(string input)
    {
        var uri = new Uri(input);
        var result = _mapper.MapUriToLocal(uri);

        result.Should().NotBeNull();
        result.Scheme.Should().Be("file");
        result.LocalPath.Should().Contain("wsl$");
    }

    [Fact]
    public void MapUriToLocal_NonFileUri_ReturnsUnchanged()
    {
        var uri = new Uri("http://example.com/test");
        var result = _mapper.MapUriToLocal(uri);

        result.Should().Be(uri);
    }

    [Fact]
    public void MapUriToRemote_NonFileUri_ReturnsUnchanged()
    {
        var uri = new Uri("http://example.com/test");
        var result = _mapper.MapUriToRemote(uri);

        result.Should().Be(uri);
    }
}

