namespace Jellyfin.Plugin.EasyNotif;

/// <summary>
/// Server identity captured once at plugin registration, so services that build outbound links
/// (deep links into the web client) do not need to resolve <c>IServerApplicationHost</c> at
/// runtime.
/// </summary>
/// <param name="SystemId">The server's stable system id (<c>serverId</c> in web deep links).</param>
/// <param name="ServerName">The server's friendly name, for email headings.</param>
public sealed record ServerLinkContext(string SystemId, string ServerName);
