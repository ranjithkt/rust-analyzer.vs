namespace KS.RustAnalyzer.TestAdapter.UnitTests.Common;

using FluentAssertions;
using KS.RustAnalyzer.TestAdapter.Common;
using Xunit;

public sealed class WslMirrorPathMapperTests
{
    private static WslMirrorConfig CreateCfg()
    {
        return new WslMirrorConfig(
            (PathEx)@"C:\Repos\proj",
            "Debian",
            "/home/u",
            "/home/u/.cache/rust-analyzer.vs/mirrors",
            "0123456789abcdef0123456789abcdef");
    }

    [Theory]
    [InlineData("build")]
    [InlineData("--manifest-path")]
    [InlineData("--package")]
    [InlineData("common")]
    [InlineData("json")]
    public void ConvertArgumentWindowsPathsToMirror_DoesNotRewrite_NonPathTokens(string token)
    {
        var cfg = CreateCfg();
        WslMirrorPathMapper.ConvertArgumentWindowsPathsToMirror(token, cfg).Should().Be(token);
    }

    [Fact]
    public void ConvertArgumentWindowsPathsToMirror_Rewrites_WindowsDrivePath()
    {
        var cfg = CreateCfg();
        var arg = @"C:\Repos\proj\Cargo.toml";

        var mapped = WslMirrorPathMapper.ConvertArgumentWindowsPathsToMirror(arg, cfg);

        mapped.Should().Be("/home/u/.cache/rust-analyzer.vs/mirrors/0123456789abcdef0123456789abcdef/win/c/Repos/proj/Cargo.toml");
    }

    [Fact]
    public void ConvertArgumentWindowsPathsToMirror_Rewrites_EmbeddedWindowsPath()
    {
        var cfg = CreateCfg();
        var arg = "--manifest-path=C:\\Repos\\proj\\Cargo.toml";

        var mapped = WslMirrorPathMapper.ConvertArgumentWindowsPathsToMirror(arg, cfg);

        mapped.Should().Be("--manifest-path=/home/u/.cache/rust-analyzer.vs/mirrors/0123456789abcdef0123456789abcdef/win/c/Repos/proj/Cargo.toml");
    }

    [Fact]
    public void TryWindowsToMirrorLinuxPath_DoesNotTreat_RelativeTokenAsPath()
    {
        var cfg = CreateCfg();

        WslMirrorPathMapper.TryWindowsToMirrorLinuxPath("build", cfg, out _).Should().BeFalse();
        WslMirrorPathMapper.TryWindowsToMirrorLinuxPath("--manifest-path", cfg, out _).Should().BeFalse();
        WslMirrorPathMapper.TryWindowsToMirrorLinuxPath("common", cfg, out _).Should().BeFalse();
    }
}
