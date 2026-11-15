namespace RunnerOrchestrator.Pool;

/// <summary>An <c>owner/name</c> pair, compared without case like GitHub does.</summary>
public readonly record struct RepositoryName(string Owner, string Name)
{
    public static RepositoryName Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return TryParse(value) ?? throw new FormatException($"'{value}' is not an owner/name repository.");
    }

    /// <summary>The parsed name, or null when the value is not <c>owner/name</c>.</summary>
    public static RepositoryName? TryParse(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var parts = value.Split('/');
        return parts.Length == 2 && parts.All(IsValidSegment) ? new RepositoryName(parts[0], parts[1]) : null;
    }

    public bool Equals(RepositoryName other) =>
        string.Equals(Owner, other.Owner, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => HashCode.Combine(
        StringComparer.OrdinalIgnoreCase.GetHashCode(Owner),
        StringComparer.OrdinalIgnoreCase.GetHashCode(Name));

    public override string ToString() => $"{Owner}/{Name}";

    private static bool IsValidSegment(string segment) =>
        segment.Trim('.').Length > 0 && segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
