using WindowsAppProtector.Models;

namespace WindowsAppProtector.Services;

public interface ISettingsStore
{
    Task<AppConfig> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppConfig config, CancellationToken cancellationToken = default);
}
