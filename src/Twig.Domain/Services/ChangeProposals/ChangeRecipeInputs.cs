namespace Twig.Domain.Services.ChangeProposals;

/// <summary>
/// The concrete inputs a Change Recipe is rendered against.
/// <para>
/// The strict accessor lives here rather than in each recipe on purpose: "missing or
/// invalid input fails loudly" is a contract every recipe shares, and a shared
/// implementation cannot be forgotten by the next recipe author the way a per-recipe
/// null-check can.
/// </para>
/// </summary>
public sealed class ChangeRecipeInputs
{
    private readonly Dictionary<string, string> _values;

    /// <summary>
    /// Creates an input set. Lookups are ordinal and case-sensitive — an input named
    /// <c>workItemId</c> is not the same input as <c>WorkItemId</c>, because silently
    /// accepting either would make rendering depend on how a caller happened to spell it.
    /// </summary>
    public ChangeRecipeInputs(IReadOnlyDictionary<string, string>? values = null)
    {
        _values = values is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(values, StringComparer.Ordinal);
    }

    /// <summary>The input names supplied, in no guaranteed order.</summary>
    public IReadOnlyCollection<string> Names => _values.Keys;

    /// <summary>
    /// Returns the value for <paramref name="name"/>, or throws when it is absent, empty,
    /// or whitespace. This is the accessor recipes should use for anything they require.
    /// </summary>
    /// <exception cref="ChangeRecipeInputException">The input is missing or blank.</exception>
    public string Require(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!_values.TryGetValue(name, out var value))
            throw new ChangeRecipeInputException(name, "input is missing");

        if (string.IsNullOrWhiteSpace(value))
            throw new ChangeRecipeInputException(name, "input is empty");

        return value;
    }

    /// <summary>
    /// Returns the value for <paramref name="name"/>, or throws when it is absent, blank, or
    /// not an integer. Use for numeric inputs such as a work item id.
    /// </summary>
    /// <exception cref="ChangeRecipeInputException">The input is missing, blank, or not an integer.</exception>
    public int RequireInt(string name)
    {
        var raw = Require(name);
        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value))
            throw new ChangeRecipeInputException(name, $"input '{raw}' is not an integer");

        return value;
    }

    /// <summary>Non-throwing lookup for genuinely optional inputs.</summary>
    public bool TryGet(string name, out string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _values.TryGetValue(name, out value!) && !string.IsNullOrWhiteSpace(value);
    }
}
