using Microsoft.Extensions.Logging.Abstractions;
using ProjectInsightMCP.Services;
using Xunit;

namespace ProjectInsightMCP.Tests;

public sealed class CredentialStorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pi-cred-tests-" + Guid.NewGuid().ToString("N"), ".projectinsight");
    private readonly CredentialStorageService _store;

    public CredentialStorageTests() => _store = new CredentialStorageService(NullLogger<CredentialStorageService>.Instance, _dir);

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_dir)!, recursive: true);

    private static StoredCredential Credential() => new()
    {
        ApiToken = "token", UserId = "u-1", ExpiresUtc = DateTime.UtcNow.AddDays(30), BaseUrl = "https://pi.test/api"
    };

    [Fact]
    public void Saved_credentials_round_trip_and_leave_no_temp_file()
    {
        _store.SaveCredential(Credential());

        Assert.Equal("token", _store.LoadCredential()!.ApiToken);
        Assert.Equal(["credentials.json"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Saved_credentials_are_readable_by_their_owner_only()
    {
        if (OperatingSystem.IsWindows()) return;

        _store.SaveCredential(Credential());

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_dir, "credentials.json")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_dir));
    }

    [Fact]
    public void Loading_tightens_a_file_an_earlier_version_left_world_readable()
    {
        if (OperatingSystem.IsWindows()) return;
        _store.SaveCredential(Credential());
        var file = Path.Combine(_dir, "credentials.json");
        var open = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        File.SetUnixFileMode(file, open);
        File.SetUnixFileMode(_dir, open | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);

        Assert.NotNull(_store.LoadCredential());

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_dir));
    }
}
