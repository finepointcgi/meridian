using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;

namespace Meridian.Tests;

/// <summary>Review rules and mention parsing. Store behaviour is covered by StoreContract (SQLite and PostgreSQL).</summary>
public sealed class ReviewStoreTests
{
    [Fact]
    public void Review_State_Follows_The_Votes()
    {
        UserAccount U(string n) => new(Guid.NewGuid(), n, n, "#fff");
        ReviewerInfo R(ReviewVote v) => new(U("x"), v, null);
        Assert.Equal(ReviewState.Open, ReviewRules.Compute(ReviewState.Open, []));
        Assert.Equal(ReviewState.Open, ReviewRules.Compute(ReviewState.Approved, [R(ReviewVote.Approve), R(ReviewVote.Pending)]));
        Assert.Equal(ReviewState.Approved, ReviewRules.Compute(ReviewState.Open, [R(ReviewVote.Approve), R(ReviewVote.Approve)]));
        Assert.Equal(ReviewState.ChangesRequested, ReviewRules.Compute(ReviewState.Open, [R(ReviewVote.Approve), R(ReviewVote.RequestChanges)]));
        Assert.Equal(ReviewState.Closed, ReviewRules.Compute(ReviewState.Closed, [R(ReviewVote.Approve)]));
    }

    [Fact]
    public void Sso_Usernames_Are_Suggested_From_Identity_Names()
    {
        Assert.Equal("alice", LoginPolicy.SuggestUsername("Alice@Example.com"));
        Assert.Equal("j.doe-x", LoginPolicy.SuggestUsername("J.Doe X@corp"));
        Assert.Equal("user", LoginPolicy.SuggestUsername("@@"));
    }

    [Fact]
    public void Mentions_Match_Members_And_Ignore_Punctuation_And_Emails()
    {
        var ann = new UserAccount(Guid.NewGuid(), "ann", "Ann Lee", "#fff");
        var jo = new UserAccount(Guid.NewGuid(), "jo.smith", "Jo Smith", "#fff");
        var people = new[] { ann, jo };
        var parts = Mentions.Split("Hi @ann, ask @jo.smith. Mail ann@example.com or @nobody.", people);
        Assert.Equal(["Hi ", "@ann", ", ask ", "@jo.smith", ". Mail ann@example.com or @nobody."], parts.Select(p => p.Text));
        Assert.Equal([ann.Id, jo.Id], parts.Where(p => p.User != null).Select(p => p.User!.Id));
        Assert.Equal([ann.Id], Mentions.Find("@ANN and @ann again.", people).Select(u => u.Id));
        Assert.Empty(Mentions.Find("no mentions here", people));
    }
}
