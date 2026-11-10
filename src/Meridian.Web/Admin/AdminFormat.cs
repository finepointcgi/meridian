using System.Globalization;

namespace Meridian.Web.Admin;

public static class AdminFormat
{
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return (i == 0 ? v.ToString("0", CultureInfo.InvariantCulture) : v.ToString(v < 10 ? "0.0" : "0", CultureInfo.InvariantCulture)) + " " + units[i];
    }

    public static string When(DateTime? utc) => utc is { } t ? t.ToLocalTime().ToString("MMM d yyyy, HH:mm", CultureInfo.InvariantCulture) : "—";

    /// <summary>A strong random password for resets and new accounts (no look-alike characters).</summary>
    public static string GeneratePassword()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return string.Concat(Enumerable.Range(0, 16).Select(i => (i is 4 or 8 or 12 ? "-" : "") + alphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(alphabet.Length)]));
    }
}
