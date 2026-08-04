using System.Collections.Immutable;
using Orvian.Plugin.Abstractions;
using Orvian.Plugins.UsersGroups;
using Xunit;

namespace Orvian.Plugins.UsersGroups.Tests;

public sealed class UsersGroupsProviderTests
{
    [Fact]
    public void ProviderUsesTwoStructuredGetentRequests()
    {
        var requests = new GetentUsersGroupsProvider().CreateRequests();

        Assert.Collection(
            requests,
            users =>
            {
                Assert.Equal("getent", users.Executable);
                Assert.Equal("passwd", Assert.Single(users.Arguments).Value);
            },
            groups =>
            {
                Assert.Equal("getent", groups.Executable);
                Assert.Equal("group", Assert.Single(groups.Arguments).Value);
            });
    }

    [Fact]
    public void UsersAndGroupsParseWithoutPasswordHashAccess()
    {
        var users =
            """
            root:x:0:0:root:/root:/bin/bash
            alice:x:1000:1000:Alice Example:/home/alice:/bin/zsh
            """;
        var groups =
            """
            root:x:0:
            developers:x:1000:alice,bob
            """;

        var result = new GetentUsersGroupsProvider().Parse(
        [
            new(0, users, string.Empty, false),
            new(0, groups, string.Empty, false)
        ]);

        Assert.True(result.IsSuccess);
        Assert.Equal("2", result.Model!.Values["User count"]);
        Assert.Contains("UID 1000", result.Model.Values["User 0002"]);
        Assert.Contains("alice, bob", result.Model.Values["Group 0002"]);
    }

    [Fact]
    public void ProviderRejectsTruncationFromEitherCommand()
    {
        var result = new GetentUsersGroupsProvider().Parse(
        [
            new(0, "root:x:0:0:root:/root:/bin/sh", string.Empty, false),
            new(0, "root:x:0:", string.Empty, true)
        ]);

        Assert.False(result.IsSuccess);
    }

    [Theory]
    [InlineData("root:x:not-a-number:0:root:/root:/bin/sh", "root:x:0:")]
    [InlineData("root:x:0:0:root:/root", "root:x:0:")]
    [InlineData("root:x:0:0:root:/root:/bin/sh\nroot:x:1:1:dup:/tmp:/bin/sh", "root:x:0:")]
    [InlineData("root:x:0:0:root:/root:/bin/sh", "developers:x:not-a-number:alice")]
    [InlineData("root:x:0:0:root:/root:/bin/sh", "developers:x:1000:alice,bad member")]
    public void MalformedIdentityRecordsFailSafely(string users, string groups)
    {
        var result = new GetentUsersGroupsProvider().Parse(
        [
            new(0, users, string.Empty, false),
            new(0, groups, string.Empty, false)
        ]);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.SafeFailureMessage);
    }

    [Fact]
    public void MultiCommandContractCannotBeCollapsedToSingleUnauditedCommand()
    {
        IReadOnlyFeatureProvider provider = new GetentUsersGroupsProvider();

        Assert.Throws<NotSupportedException>(() => provider.CreateRequest());
        Assert.Throws<NotSupportedException>(
            () => provider.Parse(new(0, string.Empty, string.Empty, false)));
    }
}
