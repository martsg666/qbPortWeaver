namespace qbPortWeaver
{
    public interface IVPNManager
    {
        string ProviderName { get; }
        bool IsVPNConnected();
        int? GetVPNPort();
    }
}
