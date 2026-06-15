using System.Text.Json;
using WindowsAppProtector.Models;

namespace WindowsAppProtector.Services;

public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string userConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WindowsAppProtector.WinUI",
        "config.json");
    private readonly string sharedConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Windows App Protector",
        "config.json");

    public async Task<AppConfig> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(userConfigPath))
        {
            await using var stream = File.OpenRead(userConfigPath);
            return await JsonSerializer.DeserializeAsync<AppConfig>(stream, JsonOptions, cancellationToken)
                ?? new AppConfig();
        }

        if (!File.Exists(sharedConfigPath))
        {
            return new AppConfig();
        }

        await using var sharedStream = File.OpenRead(sharedConfigPath);
        return await JsonSerializer.DeserializeAsync<AppConfig>(sharedStream, JsonOptions, cancellationToken)
            ?? new AppConfig();
    }

    public async Task SaveAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        await SaveToPathAsync(userConfigPath, config, cancellationToken);

        try
        {
            await SaveToPathAsync(sharedConfigPath, config, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static async Task SaveToPathAsync(string path, AppConfig config, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = path + ".tmp";
        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, config, JsonOptions, cancellationToken);
            }

            if (File.Exists(path))
            {
                File.Replace(tempPath, path, null);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        catch (UnauthorizedAccessException) when (File.Exists(path))
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            await JsonSerializer.SerializeAsync(stream, config, JsonOptions, cancellationToken);
        }
        catch (IOException) when (File.Exists(path))
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            await JsonSerializer.SerializeAsync(stream, config, JsonOptions, cancellationToken);
        }
    }
}
