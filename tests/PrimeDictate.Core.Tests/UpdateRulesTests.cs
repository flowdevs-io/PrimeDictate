using PrimeDictate.Platforms.Updates;

namespace PrimeDictate.Core.Tests;

public sealed class UpdateRulesTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string Release(string tag, bool draft = false, bool prerelease = false, bool withChecksum = true, bool withArm = true)
    {
        var assets = new List<string>
        {
            $$"""{"name":"PrimeDictate-Setup-{{tag}}-x64.msi","browser_download_url":"https://example.test/x64.msi","size":1234}""",
        };
        if (withArm)
        {
            assets.Add($$"""{"name":"PrimeDictate-Setup-{{tag}}-arm64.msi","browser_download_url":"https://example.test/arm64.msi","size":999}""");
        }

        if (withChecksum)
        {
            assets.Add($$"""{"name":"PrimeDictate-Setup-{{tag}}-x64.msi.sha256","browser_download_url":"https://example.test/x64.sha256"}""");
        }

        return $$"""
            {"tag_name":"{{tag}}","draft":{{draft.ToString().ToLowerInvariant()}},"prerelease":{{prerelease.ToString().ToLowerInvariant()}},
             "html_url":"https://example.test/release","published_at":"2026-10-01T12:00:00Z","assets":[{{string.Join(',', assets)}}]}
            """;
    }

    [Theory]
    [InlineData("v6.1.0", 6, 1, 0)]
    [InlineData("6.1.0", 6, 1, 0)]
    [InlineData("V6.10.3-beta.1", 6, 10, 3)]
    [InlineData("6.1.0+abc123", 6, 1, 0)]
    public void Release_tags_parse_to_major_minor_build(string text, int major, int minor, int build)
    {
        Assert.True(UpdateRules.TryParseReleaseVersion(text, out var version));
        Assert.Equal(new Version(major, minor, build), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v6.1")]
    public void Unparseable_tags_are_rejected(string? text) => Assert.False(UpdateRules.TryParseReleaseVersion(text, out _));

    [Fact]
    public void Versions_compare_numerically_not_as_text() =>
        Assert.True(UpdateRules.TryParseReleaseVersion("v6.10.0", out var newer) && UpdateRules.TryParseReleaseVersion("v6.9.0", out var older) && newer > older);

    [Fact]
    public void Current_version_prefers_the_informational_version()
    {
        Assert.Equal(new Version(6, 1, 0), UpdateRules.CurrentVersion("6.1.0+gitsha", new Version(1, 0, 0, 0)));
        Assert.Equal(new Version(6, 0, 0), UpdateRules.CurrentVersion(null, new Version(6, 0, 0, 0)));
        Assert.Equal(new Version(0, 0, 0), UpdateRules.CurrentVersion(null, null));
    }

    [Fact]
    public void Asset_names_match_what_the_release_workflow_publishes()
    {
        Assert.Equal("PrimeDictate-Setup-v6.1.0-x64.msi", UpdateRules.InstallerAssetName("v6.1.0", "x64"));
        Assert.Equal("PrimeDictate-Setup-v6.1.0-arm64.msi.sha256", UpdateRules.ChecksumAssetName("v6.1.0", "arm64"));
        Assert.Equal("arm64", UpdateRules.ArchitectureName(System.Runtime.InteropServices.Architecture.Arm64));
        Assert.Equal("x64", UpdateRules.ArchitectureName(System.Runtime.InteropServices.Architecture.X64));
    }

    [Fact]
    public void A_newer_release_selects_this_architectures_installer_and_checksum()
    {
        var info = UpdateRules.ReadRelease(Release("v6.2.0"), new Version(6, 1, 0), "x64");
        Assert.NotNull(info);
        Assert.Equal("6.2.0", info!.DisplayVersion);
        Assert.Equal("PrimeDictate-Setup-v6.2.0-x64.msi", info.InstallerAssetName);
        Assert.Equal("https://example.test/x64.msi", info.InstallerDownloadUrl.ToString());
        Assert.Equal("https://example.test/x64.sha256", info.Sha256DownloadUrl.ToString());
        Assert.Equal(1234, info.InstallerSizeBytes);
        Assert.NotNull(info.PublishedAt);
    }

    [Fact]
    public void Same_older_draft_and_prerelease_versions_offer_nothing()
    {
        Assert.Null(UpdateRules.ReadRelease(Release("v6.1.0"), new Version(6, 1, 0), "x64"));
        Assert.Null(UpdateRules.ReadRelease(Release("v6.0.0"), new Version(6, 1, 0), "x64"));
        Assert.Null(UpdateRules.ReadRelease(Release("v7.0.0", draft: true), new Version(6, 1, 0), "x64"));
        Assert.Null(UpdateRules.ReadRelease(Release("v7.0.0", prerelease: true), new Version(6, 1, 0), "x64"));
    }

    [Fact]
    public void A_newer_release_without_a_checksum_or_this_installer_is_an_error_not_an_offer()
    {
        Assert.Contains("sha256", Assert.Throws<InvalidOperationException>(() => UpdateRules.ReadRelease(Release("v6.2.0", withChecksum: false), new Version(6, 1, 0), "x64")).Message);
        Assert.Contains("arm64.msi", Assert.Throws<InvalidOperationException>(() => UpdateRules.ReadRelease(Release("v6.2.0", withArm: false), new Version(6, 1, 0), "arm64")).Message);
    }

    [Theory]
    [InlineData(Hash, Hash)]
    [InlineData(Hash + "  PrimeDictate-Setup-v6.1.0-x64.msi\n", Hash)]
    [InlineData("\r\n0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF *file.msi", Hash)]
    public void Checksum_files_yield_the_lowercase_hash(string text, string expected) => Assert.Equal(expected, UpdateRules.ParseSha256(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a hash")]
    [InlineData("0123456789abcdef")]
    [InlineData("zz23456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void Text_without_a_64_digit_hash_yields_nothing(string? text) => Assert.Null(UpdateRules.ParseSha256(text));

    [Fact]
    public void Automatic_checks_run_at_most_once_a_day()
    {
        var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(UpdateRules.IsCheckDue(null, now));
        Assert.False(UpdateRules.IsCheckDue(now.AddHours(-23), now));
        Assert.True(UpdateRules.IsCheckDue(now.AddHours(-24), now));
        Assert.True(UpdateRules.IsCheckDue(now.AddDays(-3), now));
    }

    [Fact]
    public void Check_state_round_trips_and_ignores_a_corrupt_file()
    {
        var path = Path.Combine(Path.GetTempPath(), "pd-upd-" + Guid.NewGuid().ToString("N"), "update-check.json");
        try
        {
            var state = new UpdateCheckState(path);
            Assert.Null(state.LastCheckUtc());
            var when = new DateTime(2026, 10, 2, 8, 30, 0, DateTimeKind.Utc);
            state.Save(when);
            Assert.Equal(when, state.LastCheckUtc());
            state.Save(null);
            Assert.Null(state.LastCheckUtc());
            File.WriteAllText(path, "{ not json");
            Assert.Null(state.LastCheckUtc());
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }
}
