using System.Collections.Immutable;
using System.Globalization;
using Orvian.Plugin.Abstractions;

namespace Orvian.Plugins.UsersGroups;

public sealed class UsersGroupsPlugin : IOrvianPlugin
{
    public PluginManifest Manifest { get; } = new(
        "orvian.users-groups",
        "Users & Groups",
        new Version(0, 1, 0),
        "Remotune",
        "Inspect remote user and group identity metadata.",
        new Version(0, 1, 0),
        [
            PluginPermissions.UiNavigationContribute,
            PluginPermissions.CommandReadExecute
        ]);

    public void Configure(IPluginBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Navigation.AddFeaturePage(
            "/users-groups",
            "Users & Groups",
            "people",
            typeof(UsersGroupsPageViewModel),
            "users-groups");
        builder.Features.Add(new GetentUsersGroupsProvider());
    }
}

public sealed class UsersGroupsPageViewModel;

public sealed class GetentUsersGroupsProvider : IMultiCommandReadFeatureProvider
{
    private const int MaximumOutputLength = 1024 * 1024;
    private const int MaximumRecords = 10000;
    private const int MaximumDisplayedPerKind = 200;
    private static readonly ImmutableHashSet<string> Capabilities =
        ImmutableHashSet.Create(StringComparer.Ordinal, "identity.getent");

    public string FeatureId => "users-groups";

    public string ProviderId => "users-groups.getent";

    public int Priority => 100;

    public IReadOnlySet<string> RequiredCapabilities => Capabilities;

    public ImmutableArray<PluginReadRequest> CreateRequests() =>
    [
        new("getent", [new("passwd")], TimeSpan.FromSeconds(15)),
        new("getent", [new("group")], TimeSpan.FromSeconds(15))
    ];

    public PluginFeatureParseResult Parse(
        ImmutableArray<PluginCommandOutput> outputs)
    {
        if (outputs.Length != 2 ||
            outputs.Any(output =>
                output.ExitCode != 0 ||
                output.IsTruncated ||
                output.StandardOutput.Length > MaximumOutputLength ||
                output.StandardOutput.Contains('\0')))
        {
            return PluginFeatureParseResult.Failure(
                "User and group identity output was incomplete.");
        }

        if (!TryParseUsers(outputs[0].StandardOutput, out var users) ||
            !TryParseGroups(outputs[1].StandardOutput, out var groups))
        {
            return PluginFeatureParseResult.Failure(
                "User or group identity output was malformed.");
        }

        var values = ImmutableDictionary.CreateBuilder<string, string>(
            StringComparer.Ordinal);
        values.Add("User count", users.Count.ToString(CultureInfo.InvariantCulture));
        values.Add("Group count", groups.Count.ToString(CultureInfo.InvariantCulture));
        for (var index = 0;
             index < Math.Min(users.Count, MaximumDisplayedPerKind);
             index++)
        {
            var user = users[index];
            values.Add(
                $"User {index + 1:D4}",
                $"{user.Name} · UID {user.UserId} · GID {user.GroupId} · " +
                $"{user.HomeDirectory} · {user.Shell}");
        }

        for (var index = 0;
             index < Math.Min(groups.Count, MaximumDisplayedPerKind);
             index++)
        {
            var group = groups[index];
            values.Add(
                $"Group {index + 1:D4}",
                $"{group.Name} · GID {group.GroupId} · members: " +
                (group.Members.Length == 0
                    ? "none listed"
                    : string.Join(", ", group.Members)));
        }

        if (users.Count > MaximumDisplayedPerKind ||
            groups.Count > MaximumDisplayedPerKind)
        {
            values.Add(
                "Display notice",
                $"Showing at most {MaximumDisplayedPerKind} users and groups.");
        }

        return new(new PluginFeatureReadModel(values.ToImmutable()));
    }

    private static bool TryParseUsers(
        string output,
        out List<UserRecord> users)
    {
        users = [];
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in Lines(output))
        {
            var fields = line.Split(':');
            if (fields.Length != 7 ||
                !IsSafeIdentity(fields[0]) ||
                !names.Add(fields[0]) ||
                !uint.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var uid) ||
                !uint.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var gid) ||
                !IsSafeText(fields[4], 1024) ||
                !IsSafeText(fields[5], 4096) ||
                !IsSafeText(fields[6], 4096) ||
                users.Count >= MaximumRecords)
            {
                return false;
            }

            users.Add(new(fields[0], uid, gid, fields[5], fields[6]));
        }

        return users.Count > 0;
    }

    private static bool TryParseGroups(
        string output,
        out List<GroupRecord> groups)
    {
        groups = [];
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in Lines(output))
        {
            var fields = line.Split(':');
            if (fields.Length != 4 ||
                !IsSafeIdentity(fields[0]) ||
                !names.Add(fields[0]) ||
                !uint.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var gid) ||
                !IsSafeText(fields[3], 8192) ||
                groups.Count >= MaximumRecords)
            {
                return false;
            }

            var members = fields[3].Length == 0
                ? []
                : fields[3].Split(',');
            if (members.Any(member => !IsSafeIdentity(member)))
            {
                return false;
            }

            groups.Add(new(fields[0], gid, members));
        }

        return groups.Count > 0;
    }

    private static IEnumerable<string> Lines(string output) =>
        output.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0);

    private static bool IsSafeIdentity(string value) =>
        value.Length is > 0 and <= 256 &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '_' or '-' or '.' or '@' or '$' or '+');

    private static bool IsSafeText(string value, int maximumLength) =>
        value.Length <= maximumLength &&
        value.All(character => !char.IsControl(character));

    private sealed record UserRecord(
        string Name,
        uint UserId,
        uint GroupId,
        string HomeDirectory,
        string Shell);

    private sealed record GroupRecord(
        string Name,
        uint GroupId,
        string[] Members);
}
