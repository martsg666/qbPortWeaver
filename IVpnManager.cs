namespace qbPortWeaver
{
    public interface IVpnManager
    {
        string ProviderName { get; }
        bool IsVPNConnected();
        int? GetVPNPort();
    }
}
