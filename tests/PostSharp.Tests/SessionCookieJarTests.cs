using PostSharp.Services;
using Xunit;

namespace PostSharp.Tests;

public class SessionCookieJarTests
{
    [Fact]
    public void CookiesCanBeReadAndClearedForTheCurrentSite()
    {
        var uri = new Uri($"https://cookie-test-{Guid.NewGuid():N}.example/api");
        SessionCookieJar.HandlerContainer.SetCookies(uri, "session=abc123; path=/; HttpOnly");

        var cookies = SessionCookieJar.GetCookies(uri);

        Assert.Single(cookies);
        Assert.Equal("session", cookies[0].Name);
        Assert.True(cookies[0].HttpOnly);
        Assert.Contains("HttpOnly", cookies[0].Flags, StringComparison.Ordinal);

        SessionCookieJar.Clear(uri);

        Assert.Empty(SessionCookieJar.GetCookies(uri));
    }

    [Fact]
    public void InvalidUrlReturnsNoCookies()
    {
        Assert.Empty(SessionCookieJar.GetCookies("not a url"));
    }
}
