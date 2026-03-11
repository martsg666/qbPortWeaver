using System.ServiceProcess;

namespace qbPortWeaver
{
    internal static class VpnManagerHelper
    {
        // Finds a Windows service by exact name, logs the result, and returns the matched service name or null.
        internal static string? FindServiceByExactName(string serviceName, string logPrefix)
        {
            ServiceController[] services = ServiceController.GetServices();
            try
            {
                string? found = services
                    .FirstOrDefault(s => s.ServiceName.Equals(serviceName, StringComparison.OrdinalIgnoreCase))
                    ?.ServiceName;
                LogManager.Instance.LogDebug(found != null
                    ? $"{logPrefix}.FindServiceName: found service '{found}'"
                    : $"{logPrefix}.FindServiceName: '{serviceName}' not found");
                return found;
            }
            finally { foreach (var s in services) s.Dispose(); }
        }
    }
}
