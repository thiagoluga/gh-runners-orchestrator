namespace RunnerOrchestrator.Pool;

/// <summary>An <c>owner/name</c> pair, compared without case like GitHub does.</summary>
public readonly record struct RepositoryName(string Owner, string Name)
{
    public static RepositoryName Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var parts = value.Split('/');
        if (parts.Length != 2 || !parts.All(IsValidSegment))
        {
            throw new FormatException($"'{value}' is not an owner/name repository.");
        }

        return new RepositoryName(parts[0], parts[1]);
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
