using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.EasyNotif.Util;

/// <summary>
/// Validation for the admin-set public base URL of the Jellyfin server
/// (<see cref="Configuration.PluginConfiguration.PublicServerUrl"/>), which is interpolated into
/// email links and the <c>List-Unsubscribe</c> header value.
/// </summary>
public static class PublicUrl
{
    /// <summary>
    /// Returns true when <paramref name="value"/> is a non-blank absolute http/https URL with no
    /// control characters. A blank value is not "valid" here: callers treat blank as "not
    /// configured" and must check for it separately when that is an acceptable state.
    /// </summary>
    /// <param name="value">The candidate URL.</param>
    /// <returns>True when the value is a safe absolute http/https URL.</returns>
    public static bool IsValid([NotNullWhen(true)] string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
        {
            return false;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}
