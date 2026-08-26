using Microsoft.Win32;
using System.ServiceProcess;

namespace qbPortWeaver;

/// <summary>Finds Windows services and resolves the executables that back them, for VPN provider
/// discovery and for locating a provider's client binary next to its service.</summary>
public static class ServiceLookup
{
    /// <summary>
    /// The service-matching rule used by <see cref="FindServiceName"/>: a service matches when its
    /// <c>ServiceName</c> or <c>DisplayName</c> contains <paramref name="searchTerm"/>, ignoring case.
    /// Shared with <see cref="VpnDetector"/>, which enumerates services itself to read their status,
    /// so the two cannot disagree about what counts as a match.
    /// </summary>
    internal static bool ServiceMatches(ServiceController service, string searchTerm) =>
        service.ServiceName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
        service.DisplayName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Searches all installed Windows services for one whose <c>ServiceName</c> or <c>DisplayName</c>
    /// contains <paramref name="searchTerm"/> and returns the <c>ServiceName</c>, or
    /// <see langword="null"/> if no match is found. When multiple services match, the first one
    /// returned by <see cref="ServiceController.GetServices()"/> is used; that order is not guaranteed
    /// to be stable across reboots, so callers should pass a precise enough search term that no more
    /// than one service can match.
    /// </summary>
    internal static string? FindServiceName(string searchTerm)
    {
        ServiceController[]? services = null;
        try
        {
            services = ServiceController.GetServices();
            return services.FirstOrDefault(s => ServiceMatches(s, searchTerm))?.ServiceName;
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogDebug($"ServiceLookup.FindServiceName: {ex.Message}");
            return null;
        }
        finally
        {
            if (services is not null)
                foreach (var s in services) s.Dispose();
        }
    }

    /// <summary>
    /// Reads the <c>ImagePath</c> for the named Windows service from the registry and returns
    /// the directory containing the service executable, or <see langword="null"/> if the
    /// service key is absent or the path cannot be resolved.
    /// </summary>
    internal static string? GetServiceExeDirectory(string serviceName)
    {
        string? exePath = GetServiceExePath(serviceName);
        return exePath is null ? null : Path.GetDirectoryName(exePath);
    }

    /// <summary>
    /// Reads the <c>ImagePath</c> for the named Windows service from the registry and returns the
    /// full path to the service executable, or <see langword="null"/> if the service key is absent
    /// or the path cannot be resolved. Handles quoted paths and trailing arguments: <c>"C:\path\exe.exe" -arg</c>.
    /// </summary>
    internal static string? GetServiceExePath(string serviceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            if (key?.GetValue("ImagePath") is not string imagePath) return null;

            imagePath = Environment.ExpandEnvironmentVariables(imagePath.Trim());
            if (imagePath.StartsWith('"'))
            {
                int end = imagePath.IndexOf('"', 1);
                imagePath = end > 0 ? imagePath[1..end] : imagePath[1..];
            }
            else if (!File.Exists(imagePath))
            {
                // Unquoted ImagePath with trailing arguments: truncate at the first space. This is
                // wrong for an unquoted path that itself contains spaces - the classic "unquoted
                // service path" misconfiguration - but it fails safe: the truncated path will not
                // exist, so FindExeInServiceDirectory returns null with its cache untouched, the next
                // cycle retries, and the registry-configured exe path is used instead. Walking
                // successive space positions would handle it, at the cost of a loop of filesystem
                // probes for a configuration Windows itself flags and that neither supported VPN
                // client produces.
                int space = imagePath.IndexOf(' ');
                if (space > 0) imagePath = imagePath[..space];
            }

            return Path.GetFullPath(imagePath);
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogDebug($"ServiceLookup.GetServiceExePath: {serviceName} - {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Resolves an executable path from the directory of a Windows service, caching the result.
    /// Returns <see langword="null"/> if the service or file is not found; on any miss or transient
    /// error the cache is left untouched (still at the caller's <see langword="null"/> or
    /// <see cref="string.Empty"/> sentinel) so the next cycle retries. Only a successful resolution
    /// is cached permanently (non-empty string).
    /// Callers may initialize the cache field to either <see langword="null"/> or
    /// <see cref="string.Empty"/> - both are treated as "not yet searched".
    /// </summary>
    internal static string? FindExeInServiceDirectory(ref string? cache, string exeFileName, Func<string?> findServiceName, string logPrefix)
    {
        // cache is { Length: > 0 } means a path was previously resolved - return it.
        // null or empty means "not yet searched" - both proceed to the lookup below.
        if (cache is { Length: > 0 }) return cache;
        try
        {
            string? serviceName = findServiceName();
            string? serviceDir = serviceName is not null ? GetServiceExeDirectory(serviceName) : null;
            if (serviceDir is null)
            {
                LogManager.Instance.LogDebug($"{logPrefix}: Service executable directory not found");
                return null;
            }

            string exePath = Path.Combine(serviceDir, exeFileName);
            if (!File.Exists(exePath))
            {
                LogManager.Instance.LogDebug($"{logPrefix}: {exeFileName} not found at: {exePath}");
                return null;
            }

            LogManager.Instance.LogDebug($"{logPrefix}: Found {exeFileName} at: {exePath}");
            return cache = exePath;
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogDebug($"{logPrefix}: {ex.Message}");
            return null; // transient error: cache left at its prior state (null or empty) so next cycle retries
        }
    }
}
