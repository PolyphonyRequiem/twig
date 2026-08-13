using System.Text.Json.Serialization;

namespace Twig.Formatters;

/// <summary>
/// Source-generated JSON context for the CLI argument binder (AB#350).
/// </summary>
/// <remarks>
/// <para>
/// ConsoleAppFramework's generated parser binds a <c>string[]</c> option by calling
/// <c>JsonSerializer.Deserialize&lt;string[]&gt;</c> whenever the value starts with
/// <c>[</c>. Twig sets <c>JsonSerializerIsReflectionEnabledByDefault=false</c> for
/// AOT, so that call throws unless the binder is handed a resolver that knows
/// <c>string[]</c>.
/// </para>
/// <para>
/// Deliberately separate from <c>TwigJsonContext</c> in Twig.Infrastructure. That
/// context describes ADO and config DTOs; <c>string[]</c> is not a Twig DTO, it is
/// an argument-parsing detail of this executable. Keeping it here means the
/// binder's needs cannot drift into the wire contract, and the coupling is one
/// small file rather than a shared surface.
/// </para>
/// </remarks>
[JsonSerializable(typeof(string[]))]
internal sealed partial class ArgumentBinderJsonContext : JsonSerializerContext;
