using WindowsAppProtector.Models;

namespace WindowsAppProtector.Services;

public interface IProtectionService
{
    Task SyncExecutionBlockRulesAsync(IEnumerable<ProtectedApp> apps, CancellationToken cancellationToken = default);
    Task StopProtectedProcessesAsync(IEnumerable<ProtectedApp> apps, CancellationToken cancellationToken = default);
}
