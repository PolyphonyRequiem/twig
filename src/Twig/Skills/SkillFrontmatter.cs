using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Twig.Skills;

/// <summary>
/// One metadata interpretation for installation and bounded discovery. Only parser
/// events are consumed: no object deserialization, tag resolution or alias expansion.
/// </summary>
internal static partial class SkillFrontmatter
{
    internal static string? ReadName(TextReader reader, bool requireDescription = false)
    {
        if (reader.ReadLine()?.TrimStart('\uFEFF') != "---") return null;
        var yaml = new StringBuilder();
        while (true)
        {
            var line = reader.ReadLine();
            if (line is null) throw Invalid("missing closing frontmatter delimiter");
            if (line == "---") break;
            if (yaml.Length + line.Length > 65536) throw Invalid("frontmatter exceeds 64 KiB");
            yaml.AppendLine(line);
        }

        try
        {
            var parser = new Parser(new StringReader(yaml.ToString()));
            parser.Consume<StreamStart>();
            parser.Consume<DocumentStart>();
            CheckNode(parser.Consume<MappingStart>());
            var keys = new HashSet<string>(StringComparer.Ordinal);
            string? name = null;
            string? description = null;
            while (!parser.TryConsume<MappingEnd>(out _))
            {
                var key = ReadKey(parser, keys);
                if (key is "name" or "description")
                {
                    var value = parser.Consume<Scalar>();
                    CheckNode(value);
                    if (key == "name")
                    {
                        // Providers differ on YAML 1.1/1.2 implicit scalar typing.
                        // Require quotes for values that either schema treats as non-strings.
                        if (value.Style == ScalarStyle.Plain && ImplicitNameValue().IsMatch(value.Value))
                            throw Invalid("name must be a string; quote implicit YAML values");
                        name = value.Value;
                    }
                    else description = value.Value;
                }
                else ReadValue(parser, 0);
            }
            parser.Consume<DocumentEnd>();
            parser.Consume<StreamEnd>();
            if (requireDescription && string.IsNullOrWhiteSpace(description))
                throw Invalid("a nonempty description is required");
            return name;
        }
        catch (YamlException e) { throw Invalid(e.Message); }
    }

    private static string ReadKey(IParser parser, HashSet<string> keys)
    {
        var key = parser.Consume<Scalar>();
        CheckNode(key);
        if (key.Value == "<<" || !keys.Add(key.Value))
            throw Invalid("duplicate keys and merge keys are not supported");
        return key.Value;
    }

    private static void ReadValue(IParser parser, int depth)
    {
        if (depth > 32) throw Invalid("metadata nesting exceeds 32 levels");
        var node = parser.Consume<ParsingEvent>();
        CheckNode(node);
        switch (node)
        {
            case Scalar:
                return;
            case MappingStart:
                var keys = new HashSet<string>(StringComparer.Ordinal);
                while (!parser.TryConsume<MappingEnd>(out _))
                {
                    ReadKey(parser, keys);
                    ReadValue(parser, depth + 1);
                }
                return;
            case SequenceStart:
                while (!parser.TryConsume<SequenceEnd>(out _)) ReadValue(parser, depth + 1);
                return;
            default:
                throw Invalid("expected a scalar, mapping or sequence");
        }
    }

    private static void CheckNode(ParsingEvent node)
    {
        if (node is AnchorAlias || node is NodeEvent n && (!n.Anchor.IsEmpty || !n.Tag.IsEmpty))
            throw Invalid("anchors, aliases and explicit tags are not supported");
    }

    [GeneratedRegex(@"\A(?:~|null|true|false|yes|no|on|off|[-+]?[0-9][0-9_]*|0[xX][0-9a-fA-F_]+|0[oO][0-7_]+|0[bB][01_]+|[0-9]{4}-[0-9]{1,2}-[0-9]{1,2})\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImplicitNameValue();

    private static SkillLifecycleException Invalid(string detail) =>
        new($"Invalid SKILL.md YAML frontmatter: {detail}. Use one unambiguous name and ordinary YAML metadata; no files changed.");
}
