using System.Security.Claims;
using Meridian.Data;
using Meridian.Web.Admin;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meridian.Web.Tests;

/// <summary>Sign in with GitLab (gitlab.com or self-managed) as an OpenID Connect provider: preset defaults and claim mapping.</summary>
public sealed class GitLabSsoTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"meridian-gitlab-sso-{Guid.NewGuid():N}.db");
    private SqliteStore _db = default!;

    public async Task InitializeAsync()
    {
        _db = new SqliteStore(_path);
        await _db.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        return Task.CompletedTask;
    }

    private static AdminSettings Settings(params (string Key, string Value)[] values) =>
        AdminSettings.From(new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build());

    [Fact]
    public void A_Provider_Named_GitLab_Gets_GitLab_Com_Defaults()
    {
        var p = Assert.Single(Settings(("Meridian:Sso:Providers:0:Name", "gitlab"), ("Meridian:Sso:Providers:0:ClientId", "app-id")).Sso.Providers);
        Assert.Equal(("https://gitlab.com", "GitLab", "oidc-gitlab"), (p.Authority, p.Label, p.Scheme));
        Assert.Equal(["openid", "profile", "email"], p.Scopes);
        Assert.Equal(("preferred_username", "nickname", "name"), (p.ClaimMappings.Username, p.ClaimMappings.UsernameFallback, p.ClaimMappings.DisplayName));
    }

    [Fact]
    public void A_Self_Managed_GitLab_Preset_Keeps_Explicit_Settings()
    {
        var p = Assert.Single(Settings(
            ("Meridian:Sso:Providers:0:Name", "corp"), ("Meridian:Sso:Providers:0:Preset", "GitLab"),
            ("Meridian:Sso:Providers:0:Authority", "https://gitlab.acme.test/"), ("Meridian:Sso:Providers:0:ClientId", "app-id"),
            ("Meridian:Sso:Providers:0:DisplayName", "ACME GitLab"),
            ("Meridian:Sso:Providers:0:ClaimMappings:AdminClaim", "groups"), ("Meridian:Sso:Providers:0:ClaimMappings:AdminValue", "acme/meridian-admins")).Sso.Providers);
        Assert.Equal(("https://gitlab.acme.test", "ACME GitLab", "oidc-corp", "gitlab"), (p.Authority, p.Label, p.Scheme, p.Preset));
        Assert.Equal("groups", p.ClaimMappings.AdminClaim);
        // Other providers are untouched.
        var entra = Assert.Single(Settings(("Meridian:Sso:Providers:0:Name", "entra"), ("Meridian:Sso:Providers:0:Authority", "https://login.microsoftonline.com/t/v2.0"),
            ("Meridian:Sso:Providers:0:ClientId", "x")).Sso.Providers);
        Assert.Null(entra.ClaimMappings.UsernameFallback);
        Assert.Null(entra.Preset);
    }

    /// <summary>Claims as GitLab's ID token / userinfo carries them for a gitlab.com user.</summary>
    private static ClaimsPrincipal GitLabUser(bool withPreferredUsername = true, string? groupsClaim = null)
    {
        var claims = new List<Claim>
        {
            new("iss", "https://gitlab.com"), new("sub", "1234567"), new("sub_legacy", "abcdef0123456789"),
            new("name", "Ann Lee"), new("nickname", "annlee"), new("email", "ann@acme.test"), new("email_verified", "true"),
            new("profile", "https://gitlab.com/annlee"), new("picture", "https://gitlab.com/uploads/-/system/user/avatar/1234567/avatar.png"),
            new("groups", "acme"), new("groups", "acme/meridian-admins"),
        };
        if (withPreferredUsername) claims.Add(new("preferred_username", "annlee"));
        if (groupsClaim != null) claims.Add(new("groups", groupsClaim));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "oidc-gitlab", "name", "role"));
    }

    private SsoProvisioner Provisioner(AdminSettings settings) =>
        new(_db, new AuditLog(_db, NullLogger<AuditLog>.Instance), settings, new AdminAccess(_db, new MeridianHostInfo(false, "", null)));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]   // older GitLab without preferred_username: the nickname claim, not the e-mail
    public async Task GitLab_Claims_Map_To_The_GitLab_Username_And_Display_Name(bool withPreferredUsername)
    {
        var settings = Settings(("Meridian:Sso:Providers:0:Name", "gitlab"), ("Meridian:Sso:Providers:0:ClientId", "app-id"),
            ("Meridian:Sso:Providers:0:ClaimMappings:AdminClaim", "groups"), ("Meridian:Sso:Providers:0:ClaimMappings:AdminValue", "acme/meridian-admins"));
        await _db.CreateUserAsync("someone", "Someone", "correct horse");   // not the first account, so admin comes from the group
        var p = settings.Sso.Providers[0];

        var outcome = await Provisioner(settings).ResolveAsync(p, GitLabUser(withPreferredUsername), "127.0.0.1");

        Assert.Null(outcome.Error);
        Assert.True(outcome.Provisioned);
        Assert.Equal(("annlee", "Ann Lee"), (outcome.User!.Username, outcome.User.DisplayName));
        Assert.True((await _db.GetUserAsync(outcome.User.Id))!.IsAdmin);   // groups claim = acme/meridian-admins
        // The second sign-in finds the same account by issuer + subject.
        var again = await Provisioner(settings).ResolveAsync(p, GitLabUser(withPreferredUsername), "127.0.0.1");
        Assert.Equal(outcome.User.Id, again.User!.Id);
        Assert.False(again.Provisioned);
    }
}
