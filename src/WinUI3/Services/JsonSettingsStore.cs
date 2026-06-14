using System.Text.Json;
using WindowsAppProtector.Models;

namespace WindowsAppProtector.Services;

public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string configPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WindowsAppProtector.WinUI",
        "config.json");
    private readonly string serviceConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Windows App Protector",
        "config.json");

    public async Task<AppConfig> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(configPath))
        {
            return new AppConfig();
        }

        await using var stream = File.OpenRead(configPath);
        return await JsonSerializer.DeserializeAsync<AppConfig>(stream, JsonOptions, cancellationToken)
            ?? new AppConfig();
    }

    public async Task SaveAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        await using var stream = File.Create(configPath);
        await JsonSerializer.SerializeAsync(stream, config, JsonOptions, cancellationToken);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(serviceConfigPath)!);
            await using var serviceStream = File.Create(serviceConfigPath);
            await JsonSerializer.SerializeAsync(serviceStream, config, JsonOptions, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }
    }
}
