namespace qbPortWeaver.Shared;

/// <summary>
/// Wire-protocol constants for the named pipe between the user-session tray app
/// (qbPortWeaver.exe) and the SYSTEM helper service (qbPortWeaver.HelperService.exe).
/// Both processes reference these constants directly, so the protocol cannot drift.
/// <para>A request is three pipe-separated fields - action, target, session token - and a response is
/// <c>key=value</c> pairs separated by the same character.</para>
/// <para><b>The request format is frozen at three fields and a new one must not be added</b>, because
/// the two sides upgrade independently: the helper is a Windows service that an installed machine may
/// still be running from an earlier release, and it parses requests with <c>Split('|', 3)</c>,
/// requiring exactly three parts. A field added at the front shifts the action out of position; the
/// old helper still counts three parts, falls through to its unknown-action branch, and then
/// <b>still writes its normal success response</b>, because that write sits outside the action
/// switch - so the caller reads a clean result for a recovery that never ran. A field added at the
/// end lands inside the session token, failing the token check and returning
/// <see cref="ResultRejectedSentinel"/>, which misreports an out-of-date helper as a security
/// rejection.</para>
/// <para>Responses carry no such constraint: the client reads them as <c>key=value</c> pairs and
/// skips keys it does not recognise, so <b>response fields are append-only and safe to extend</b>.
/// That is how <see cref="ResultVersionKey"/> was added, and how anything further should be. New
/// information that must travel in the other direction needs a new action name, which an old helper
/// rejects loudly rather than silently.</para>
/// </summary>
public static class HelperProtocol
{
    /// <summary>Named pipe used by the tray app to send action requests to the helper service.</summary>
    public const string PipeName = "qbPortWeaverHelper";

    /// <summary>
    /// Windows service name of the helper service, also used as its EventLog source. The same
    /// string as <see cref="PipeName"/> by construction, kept as its own constant so the call
    /// sites that talk to the SCM read as service lookups rather than as a pipe name in the
    /// wrong place.
    /// <para>The value is duplicated a third time in <c>installer/qbPortWeaver.wxs</c>, which
    /// cannot reference this constant. All of them must stay identical: the name is baked into
    /// the SCM registration of every installed machine, so changing it here would break service
    /// discovery and the diagnostics helper-service check against an already-installed service.</para>
    /// </summary>
    public const string ServiceName = PipeName;

    /// <summary>Restart action: target is a Windows service name; helper stops and starts it.</summary>
    public const string ActionRestart = "restart";

    /// <summary>Cycle-adapter action: target is a network adapter name; helper disables and re-enables it via netsh.</summary>
    public const string ActionCycleAdapter = "cycle-adapter";

    /// <summary>Result key: number of WARN-level entries the helper wrote during the action.</summary>
    public const string ResultWarnKey = "warn";

    /// <summary>Result key: number of ERROR-level entries the helper wrote during the action.</summary>
    public const string ResultErrorKey = "error";

    /// <summary>Result sentinel: the helper rejected the request (session token mismatch).</summary>
    public const string ResultRejectedSentinel = "rejected";

    /// <summary>
    /// Protocol version this build speaks. The helper reports it on every response as
    /// <see cref="ResultVersionKey"/>; a response without that key came from a helper built before
    /// versioning existed, which is how an out-of-date peer is told apart from a broken one.
    /// <para><b>Version 1</b> is the original three-field request with a version-carrying response.
    /// Bump this only when the message format actually changes, and say what changed here.</para>
    /// </summary>
    public const int Version = 1;

    /// <summary>Result key: the protocol version the helper speaks. Absent on pre-versioning helpers.</summary>
    public const string ResultVersionKey = "v";
}
