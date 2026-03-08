namespace qbPortWeaver
{
    public interface IVpnManager
    {
        string ProviderName { get; }
        bool IsVpnConnected();
        Task<int?> GetVpnPortAsync();
    }
}
