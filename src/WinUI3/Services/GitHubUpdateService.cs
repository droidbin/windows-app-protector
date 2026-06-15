using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace WindowsAppProtector.Services;

public sealed class GitHubUpdateService
{
    private const string Owner = "droidbin";
    private const string Repository = "windows-app-protector";
    private const string ApiUrl = "https://api.github.com/repos/" + Owner + "/" + Repository + "/releases/latest";
    private const string TokenEnvironmentVariable = "WINDOWS_APP_PROTECTOR_GITHUB_TOKEN";
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        using var response = await HttpClient.GetAsync(ApiUrl, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return UpdateCheckResult.Failed(
                "GitHub에 배포된 최신 릴리즈가 없습니다. 저장소의 Releases에 v버전 태그와 Setup.exe를 포함한 published release를 먼저 만들어야 합니다.");
        }

        if (!response.IsSuccessStatusCode)
        {
            return UpdateCheckResult.Failed(
                $"GitHub 릴리즈 정보를 확인할 수 없습니다. ({(int)response.StatusCode})");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        var tagName = GetString(root, "tag_name");
        if (!TryParseVersion(tagName, out var latestVersion))
        {
            return UpdateCheckResult.Failed("GitHub 릴리즈 버전을 해석할 수 없습니다.");
        }

        var asset = FindInstallerAsset(root);
        if (asset is null)
        {
            return UpdateCheckResult.Failed("릴리즈에서 Setup.exe 또는 WindowsAppProtector.zip을 찾을 수 없습니다.");
        }

        var release = new GitHubReleaseInfo(
            latestVersion,
            tagName,
            GetString(root, "name"),
            GetString(root, "html_url"),
            asset.Value.Name,
            asset.Value.DownloadUrl);

        return latestVersion.CompareTo(NormalizeVersion(CurrentVersion)) > 0
            ? UpdateCheckResult.Available(release)
            : UpdateCheckResult.NotAvailable(release);
    }

    public async Task<string> DownloadInstallerAsync(
        GitHubReleaseInfo release,
        CancellationToken cancellationToken = default)
    {
        var updateDirectory = Path.Combine(
            Path.GetTempPath(),
            "WindowsAppProtector",
            "Updates",
            release.LatestVersion.ToString());

        if (Directory.Exists(updateDirectory))
        {
            Directory.Delete(updateDirectory, recursive: true);
        }

        Directory.CreateDirectory(updateDirectory);

        var assetPath = Path.Combine(updateDirectory, release.AssetName);
        using (var response = await HttpClient.GetAsync(release.AssetDownloadUrl, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var downloadStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = File.Create(assetPath);
            await downloadStream.CopyToAsync(fileStream, cancellationToken);
        }

        if (Path.GetExtension(assetPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return assetPath;
        }

        if (Path.GetExtension(assetPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var extractDirectory = Path.Combine(updateDirectory, "package");
            ZipFile.ExtractToDirectory(assetPath, extractDirectory);
            var setupPath = Directory
                .EnumerateFiles(extractDirectory, "Setup.exe", SearchOption.AllDirectories)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(setupPath))
            {
                return setupPath;
            }
        }

        throw new InvalidOperationException("다운로드한 업데이트 패키지에서 Setup.exe를 찾을 수 없습니다.");
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WindowsAppProtector", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        var token = ReadGitHubToken();
        if (!string.IsNullOrWhiteSpace(token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    private static string ReadGitHubToken()
    {
        var token = Environment.GetEnvironmentVariable(TokenEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(token))
        {
            return token.Trim();
        }

        try
        {
            var tokenPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Windows App Protector",
                "github-token.txt");
            return File.Exists(tokenPath) ? File.ReadAllText(tokenPath).Trim() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static (string Name, string DownloadUrl)? FindInstallerAsset(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        (string Name, string DownloadUrl)? zipAsset = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = GetString(asset, "name");
            var downloadUrl = GetString(asset, "browser_download_url");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(downloadUrl))
            {
                continue;
            }

            if (name.Equals("Setup.exe", StringComparison.OrdinalIgnoreCase))
            {
                return (name, downloadUrl);
            }

            if (name.Equals("WindowsAppProtector.zip", StringComparison.OrdinalIgnoreCase))
            {
                zipAsset = (name, downloadUrl);
            }
        }

        return zipAsset;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? string.Empty
                : string.Empty;
    }

    private static bool TryParseVersion(string tagName, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        var normalized = tagName.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[1..];
        }

        var metadataStart = normalized.IndexOfAny(new[] { '-', '+' });
        if (metadataStart >= 0)
        {
            normalized = normalized[..metadataStart];
        }

        return Version.TryParse(normalized, out version!);
    }

    private static Version NormalizeVersion(Version version)
    {
        return new Version(
            Math.Max(version.Major, 0),
            Math.Max(version.Minor, 0),
            Math.Max(version.Build, 0),
            Math.Max(version.Revision, 0));
    }
}

public sealed record GitHubReleaseInfo(
    Version LatestVersion,
    string TagName,
    string ReleaseName,
    string ReleasePageUrl,
    string AssetName,
    string AssetDownloadUrl);

public sealed class UpdateCheckResult
{
    private UpdateCheckResult(bool isUpdateAvailable, GitHubReleaseInfo? release, string errorMessage)
    {
        IsUpdateAvailable = isUpdateAvailable;
        Release = release;
        ErrorMessage = errorMessage;
    }

    public bool IsUpdateAvailable { get; }

    public GitHubReleaseInfo? Release { get; }

    public string ErrorMessage { get; }

    public static UpdateCheckResult Available(GitHubReleaseInfo release)
    {
        return new UpdateCheckResult(true, release, string.Empty);
    }

    public static UpdateCheckResult NotAvailable(GitHubReleaseInfo release)
    {
        return new UpdateCheckResult(false, release, string.Empty);
    }

    public static UpdateCheckResult Failed(string errorMessage)
    {
        return new UpdateCheckResult(false, null, errorMessage);
    }
}
