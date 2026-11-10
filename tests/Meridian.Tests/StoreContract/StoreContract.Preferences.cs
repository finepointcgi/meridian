using Meridian.Data;

namespace Meridian.Tests.StoreContract;

/// <summary>Per-user preferences, profile edits and "sign out everywhere".</summary>
public abstract partial class StoreContract
{
    [SkippableFact]
    public async Task Preferences_Start_Empty_Then_Round_Trip_Per_User()
    {
        await using var h = await OpenAsync();
        var s = h.Store;
        var ann = await s.CreateUserAsync("ann", "Ann", "password-1");
        var bob = await s.CreateUserAsync("bob", "Bob", "password-1");
        Assert.Null(await s.GetUserPreferencesAsync(ann.Id));

        await s.SaveUserPreferencesAsync(ann.Id, """{"theme":"light","accent":"#e5484d","favorites":["a","b"]}""");
        SameJson("""{"theme":"light","accent":"#e5484d","favorites":["a","b"]}""", await s.GetUserPreferencesAsync(ann.Id));
        Assert.Null(await s.GetUserPreferencesAsync(bob.Id));

        await s.SaveUserPreferencesAsync(ann.Id, """{"theme":"system"}""");   // replaces, not merges
        SameJson("""{"theme":"system"}""", await s.GetUserPreferencesAsync(ann.Id));
        SameJson("""{"theme":"system"}""", await h.OpenAnotherStore().GetUserPreferencesAsync(ann.Id));   // another server process / device
    }

    [SkippableFact]
    public async Task Preferences_Reject_Non_Objects_And_Oversized_Documents()
    {
        await using var h = await OpenAsync();
        var ann = await h.Store.CreateUserAsync("ann", "Ann", "password-1");
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Store.SaveUserPreferencesAsync(ann.Id, "[1,2]"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Store.SaveUserPreferencesAsync(ann.Id, "{not json"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Store.SaveUserPreferencesAsync(ann.Id, "{\"x\":\"" + new string('a', Preferences.MaxJsonLength) + "\"}"));
        Assert.Null(await h.Store.GetUserPreferencesAsync(ann.Id));
    }

    [SkippableFact]
    public async Task Preferences_Are_Deleted_With_The_User_And_Survive_Remigration()
    {
        await using var h = await OpenAsync();
        var ann = await h.Store.CreateUserAsync("ann", "Ann", "password-1");
        await h.Store.SaveUserPreferencesAsync(ann.Id, """{"density":"compact"}""");
        await h.Store.MigrateAsync();
        SameJson("""{"density":"compact"}""", await h.Store.GetUserPreferencesAsync(ann.Id));
        await h.ExecRawAsync($"delete from users where username = 'ann'");
        Assert.Equal(0L, Convert.ToInt64(await h.ScalarRawAsync("select count(*) from user_preferences")));
    }

    [SkippableFact]
    public async Task Profile_Update_Changes_Name_And_Colour_And_Validates()
    {
        await using var h = await OpenAsync();
        var s = h.Store;
        var ann = await s.CreateUserAsync("ann", "Ann", "password-1");
        var updated = await s.UpdateUserProfileAsync(ann.Id, "  Ann Example ", "#E5484D");
        Assert.Equal(new UserAccount(ann.Id, "ann", "Ann Example", "#e5484d"), updated);
        Assert.Equal(updated, await s.FindUserAsync("ann"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.UpdateUserProfileAsync(ann.Id, "", "#e5484d"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.UpdateUserProfileAsync(ann.Id, "Ann", "red; background:url(x)"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.UpdateUserProfileAsync(Guid.NewGuid(), "Ghost", "#000000"));
    }

    [SkippableFact]
    public async Task Sign_Out_Everywhere_Records_When_And_Keeps_Preferences()
    {
        await using var h = await OpenAsync();
        var s = h.Store;
        var ann = await s.CreateUserAsync("ann", "Ann", "password-1");
        Assert.Null(await s.UserSessionsRevokedAtAsync(ann.Id));

        await s.RevokeUserSessionsAsync(ann.Id);   // no preferences row yet
        var first = await s.UserSessionsRevokedAtAsync(ann.Id);
        Assert.NotNull(first);
        IsRecentUtc(first!.Value);
        Assert.Null(await s.GetUserPreferencesAsync(ann.Id) is { } j && j != "{}" ? j : null);

        await s.SaveUserPreferencesAsync(ann.Id, """{"theme":"dark"}""");
        await Task.Delay(20);
        await s.RevokeUserSessionsAsync(ann.Id);
        Assert.True((await s.UserSessionsRevokedAtAsync(ann.Id))!.Value >= first.Value);
        SameJson("""{"theme":"dark"}""", await s.GetUserPreferencesAsync(ann.Id));
        await s.SaveUserPreferencesAsync(ann.Id, """{"theme":"light"}""");
        Assert.NotNull(await s.UserSessionsRevokedAtAsync(ann.Id));   // saving preferences keeps the revocation
    }
}
