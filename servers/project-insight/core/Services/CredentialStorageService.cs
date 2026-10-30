using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ProjectInsightMCP.Services;

/// <summary>
/// Stores and retrieves PI credentials from ~/.projectinsight/credentials.json
/// </summary>
public class CredentialStorageService
{
    private readonly ILogger<CredentialStorageService> _logger;
    private readonly string CredentialDir;
    private readonly string CredentialFile;

    public static readonly string DefaultDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".projectinsight");

    // The file holds a year-long PI token: owner-only, like an SSH key.
    private const UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerDir = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public CredentialStorageService(ILogger<CredentialStorageService> logger) : this(logger, DefaultDir)
    {
    }

    /// <summary>For tests: keep credentials in <paramref name="directory"/> instead of ~/.projectinsight.</summary>
    public CredentialStorageService(ILogger<CredentialStorageService> logger, string directory)
    {
        _logger = logger;
        CredentialDir = directory;
        CredentialFile = Path.Combine(directory, "credentials.json");
    }

    /// <summary>
    /// Load stored credentials, or null if none exist or expired
    /// </summary>
    public StoredCredential? LoadCredential()
    {
        try
        {
            if (!File.Exists(CredentialFile))
                return null;

            Restrict();
            var json = File.ReadAllText(CredentialFile);
            var credential = JsonSerializer.Deserialize<StoredCredential>(json, JsonOptions);

            if (credential == null || string.IsNullOrEmpty(credential.ApiToken))
                return null;

            if (credential.ExpiresUtc.HasValue && credential.ExpiresUtc.Value < DateTime.UtcNow)
            {
                _logger.LogWarning("Stored PI credential has expired");
                return null;
            }

            _logger.LogInformation("Loaded stored PI credential for user {UserId}", credential.UserId);
            return credential;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load stored credentials");
            return null;
        }
    }

    /// <summary>
    /// Save a credential to disk
    /// </summary>
    public void SaveCredential(StoredCredential credential)
    {
        // Created owner-only and renamed into place, so the token is never readable by others, even briefly.
        // The temp name is random and must not exist yet, so a file or link left at a known name is never written through.
        var temp = $"{CredentialFile}.{Guid.NewGuid():N}.tmp";
        try
        {
            if (OperatingSystem.IsWindows())
                Directory.CreateDirectory(CredentialDir);
            else
                Directory.CreateDirectory(CredentialDir, OwnerDir);
            var json = JsonSerializer.Serialize(credential, JsonOptions);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = OwnerFile;
            using (var stream = new FileStream(temp, options))
            using (var writer = new StreamWriter(stream))
                writer.Write(json);
            Restrict(temp);
            File.Move(temp, CredentialFile, overwrite: true);
            Restrict();
            _logger.LogInformation("Saved PI credential for user {UserId}", credential.UserId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save credentials");
            throw;
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not remove a temporary credentials file");
            }
        }
    }

    /// <summary>
    /// Tighten the folder to 0700 and the file to 0600. Files saved by earlier versions were
    /// readable by every account on the machine; this fixes them the next time they're loaded.
    /// </summary>
    private void Restrict(string? file = null)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            if ((File.GetUnixFileMode(CredentialDir) & ~OwnerDir) != 0)
                File.SetUnixFileMode(CredentialDir, OwnerDir);
            file ??= CredentialFile;
            if (File.Exists(file) && (File.GetUnixFileMode(file) & ~OwnerFile) != 0)
                File.SetUnixFileMode(file, OwnerFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not restrict permissions on stored credentials");
        }
    }
}

public class StoredCredential
{
    [JsonPropertyName("apiToken")]
    public string? ApiToken { get; set; }

    [JsonPropertyName("userId")]
    public string? UserId { get; set; }

    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; set; }

    [JsonPropertyName("expiresUtc")]
    public DateTime? ExpiresUtc { get; set; }

    [JsonPropertyName("baseUrl")]
    public string? BaseUrl { get; set; }

    [JsonPropertyName("createdUtc")]
    public DateTime? CreatedUtc { get; set; }
}
