namespace EveryApp.Core.Scheduling;

/// <summary>The physical devices a path depends on, as ids like "disk:3" or "smb:nas". Two jobs conflict when their sets share an id.</summary>
public sealed record DeviceSet(IReadOnlySet<string> Ids)
{
    public bool Overlaps(DeviceSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var (small, large) = Ids.Count <= other.Ids.Count ? (Ids, other.Ids) : (other.Ids, Ids);
        foreach (var id in small)
        {
            if (large.Contains(id))
            {
                return true;
            }
        }

        return false;
    }

    public static DeviceSet Of(params string[] ids) => new(new HashSet<string>(ids, StringComparer.Ordinal));

    /// <summary>A set holding every id of both sets.</summary>
    public DeviceSet Union(DeviceSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var all = new HashSet<string>(Ids, StringComparer.Ordinal);
        all.UnionWith(other.Ids);
        return new DeviceSet(all);
    }

    public bool Equals(DeviceSet? other) => other is not null && Ids.SetEquals(other.Ids);

    public override int GetHashCode()
    {
        var hash = 0;
        foreach (var id in Ids)
        {
            hash ^= id.GetHashCode(StringComparison.Ordinal); // order-independent
        }

        return hash;
    }

    public override string ToString() => string.Join(", ", Ids.Order(StringComparer.Ordinal));
}
