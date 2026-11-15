using CodeGraph.Indexer.Lsp;

namespace CodeGraph.Tests;

/// <summary>
/// File URIs from a language server back to the paths the graph stores. On Windows, the URI code bundled in
/// typescript-language-server lower-cases the drive letter and percent-encodes its colon (file:///d%3A/…), which
/// .NET read as D:\d:\…, so every reference missed its enclosing member: the Windows CI runs of 2026-11-15 got
/// [null, null] for the live TypeScript references.
/// </summary>
public sealed class LspPathTests
{
    [Fact]
    public void A_lower_case_drive_letter_comes_back_upper_case_on_windows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "drive letters exist only on Windows");
        Assert.Equal(@"D:\a\src\app\user.service.ts", LspClient.PathOf("file:///d%3A/a/src/app/user.service.ts"));
        Assert.Equal(@"D:\a\src\app\user.service.ts", LspClient.PathOf("file:///D:/a/src/app/user.service.ts"));
    }

    [Fact]
    public void A_servers_windows_uri_is_keyed_as_ours()
    {
        // Diagnostics arrive under the server's URI and are looked up under UriOf: the two must agree.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "drive letters exist only on Windows");
        Assert.Equal(LspClient.UriOf(@"C:\Users\a\shop\src\app\user.service.ts"),
            LspClient.CanonicalUri("file:///c%3A/Users/a/shop/src/app/user.service.ts"));
    }

    [Fact]
    public void A_canonical_uri_is_unchanged_by_canonicalising_it_again()
    {
        var uri = LspClient.UriOf(Path.Combine(Path.GetTempPath(), "codegraph-lsp", "user.service.ts"));
        Assert.Equal(uri, LspClient.CanonicalUri(uri));
        Assert.Equal("untitled:Untitled-1", LspClient.CanonicalUri("untitled:Untitled-1"));
    }

    [Fact]
    public void A_file_uri_round_trips_to_its_full_path()
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "codegraph-lsp", "user.service.ts"));
        Assert.Equal(path, LspClient.PathOf(LspClient.UriOf(path)));
    }

    [Fact]
    public void A_uri_that_is_not_a_file_comes_back_unchanged()
    {
        Assert.Equal("untitled:Untitled-1", LspClient.PathOf("untitled:Untitled-1"));
    }
}
