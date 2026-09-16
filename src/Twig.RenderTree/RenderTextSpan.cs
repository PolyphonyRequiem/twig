namespace Twig.RenderTree;

/// <summary>Presentation intent for an inline value; independent of any terminal palette.</summary>
public enum RenderTextRole { Plain, Before, After }

/// <summary>Literal text plus semantic emphasis. Never ANSI or markup.</summary>
public sealed record RenderTextSpan(string Text, RenderTextRole Role = RenderTextRole.Plain);
