using System.Text;
using System.Text.Json;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig ohmyposh init</c>: generates shell hook functions and
/// Oh My Posh <c>text</c> segment JSON snippets for prompt integration.
/// </summary>
internal sealed class OhMyPoshCommands
{
    private const string Template = "{{ if .Env.TWIG_PROMPT }} {{ .Env.TWIG_PROMPT }} {{ end }}";
    private const string DefaultForeground = "#ffffff";
    private const string AzureBlue = "#0078D4";
    private const string PowerlineSymbol = "\uE0B0";
    private const string LeadingDiamond = "\uE0B6";
    private const string TrailingDiamond = "\uE0B4";

    /// <summary>
    /// Generates shell hook function and Oh My Posh text segment JSON.
    /// </summary>
    public int Init(string style = "powerline", string shell = "pwsh")
    {
        var hookOutput = GenerateShellHook(shell);
        var jsonOutput = GenerateSegmentJson(style);

        Console.WriteLine(hookOutput);
        Console.WriteLine();
        Console.WriteLine(jsonOutput);
        return 0;
    }

    internal static string GenerateShellHook(string shell)
    {
        return shell.ToLowerInvariant() switch
        {
            "pwsh" => GeneratePowerShellHook(),
            "bash" => GenerateBashHook(),
            "zsh" => GenerateZshHook(),
            "fish" => GenerateFishHook(),
            _ => GeneratePowerShellHook()
        };
    }

    internal static string GenerateSegmentJson(string style)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

        writer.WriteStartObject();
        writer.WriteString("type", "text");

        switch (style.ToLowerInvariant())
        {
            case "plain":
                WritePlainStyle(writer);
                break;
            case "diamond":
                WriteDiamondStyle(writer);
                break;
            default: // powerline
                WritePowerlineStyle(writer);
                break;
        }

        writer.WriteString("template", Template);
        WriteCache(writer);
        writer.WriteEndObject();
        writer.Flush();

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WritePowerlineStyle(Utf8JsonWriter writer)
    {
        writer.WriteString("style", "powerline");
        writer.WriteString("powerline_symbol", PowerlineSymbol);
        writer.WriteString("foreground", DefaultForeground);
        writer.WriteString("background", AzureBlue);
    }

    private static void WritePlainStyle(Utf8JsonWriter writer)
    {
        writer.WriteString("style", "plain");
        writer.WriteString("foreground", AzureBlue);
    }

    private static void WriteDiamondStyle(Utf8JsonWriter writer)
    {
        writer.WriteString("style", "diamond");
        writer.WriteString("leading_diamond", LeadingDiamond);
        writer.WriteString("trailing_diamond", TrailingDiamond);
        writer.WriteString("foreground", DefaultForeground);
        writer.WriteString("background", AzureBlue);
    }

    private static void WriteCache(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("cache");
        writer.WriteString("duration", "30s");
        writer.WriteString("strategy", "folder");
        writer.WriteEndObject();
    }

    private static string GeneratePowerShellHook()
    {
        return """
            function Set-TwigPrompt {
                $f = Join-Path $PWD ".twig" "prompt.json"
                if (Test-Path $f) {
                    $p = Get-Content $f -Raw | ConvertFrom-Json
                    $env:TWIG_PROMPT = $p.text
                    $env:TWIG_TYPE_COLOR = $p.typeColor
                    $env:TWIG_STATE_CATEGORY = $p.stateCategory
                } else {
                    $env:TWIG_PROMPT = ""
                    $env:TWIG_TYPE_COLOR = ""
                    $env:TWIG_STATE_CATEGORY = ""
                }
            }
            New-Alias -Name 'Set-PoshContext' -Value 'Set-TwigPrompt' -Scope Global -Force
            """;
    }

    private static string GenerateBashHook()
    {
        return """
            set_poshcontext() {
                local f="$PWD/.twig/prompt.json"
                if [ -f "$f" ]; then
                    export TWIG_PROMPT=$(jq -r '.text // empty' "$f" 2>/dev/null)
                    export TWIG_TYPE_COLOR=$(jq -r '.typeColor // empty' "$f" 2>/dev/null)
                    export TWIG_STATE_CATEGORY=$(jq -r '.stateCategory // empty' "$f" 2>/dev/null)
                else
                    export TWIG_PROMPT=""
                    export TWIG_TYPE_COLOR=""
                    export TWIG_STATE_CATEGORY=""
                fi
            }
            """;
    }

    // Zsh and bash use identical hook syntax.
    private static string GenerateZshHook() => GenerateBashHook();

    private static string GenerateFishHook()
    {
        return """
            function set_poshcontext
                set -l f "$PWD/.twig/prompt.json"
                if test -f "$f"
                    set -gx TWIG_PROMPT (jq -r '.text // empty' "$f" 2>/dev/null)
                    set -gx TWIG_TYPE_COLOR (jq -r '.typeColor // empty' "$f" 2>/dev/null)
                    set -gx TWIG_STATE_CATEGORY (jq -r '.stateCategory // empty' "$f" 2>/dev/null)
                else
                    set -gx TWIG_PROMPT ""
                    set -gx TWIG_TYPE_COLOR ""
                    set -gx TWIG_STATE_CATEGORY ""
                end
            end
            """;
    }
}
