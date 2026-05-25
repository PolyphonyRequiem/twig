namespace Twig.Formatters;

/// <summary>
/// Abstraction for thin universal message-formatting (errors, info, hints,
/// success, disambiguation lists). All structured data-shape output has been
/// migrated to the <c>RenderTree → IRenderer</c> seam (AB#3301); the legacy
/// machine-format implementations (JSON, JsonCompact, Minimal, Ids) have
/// been retired and <see cref="HumanOutputFormatter"/> is the sole
/// implementation. Commands that need rich machine output build a
/// <c>RenderTree</c> and resolve an <c>IRenderer</c> via <c>RendererFactory</c>.
/// </summary>
public interface IOutputFormatter
{
    string FormatError(string message);
    string FormatSuccess(string message);
    string FormatHint(string hint);
    string FormatInfo(string message);
    string FormatDisambiguation(IReadOnlyList<(int Id, string Title)> matches);
}
