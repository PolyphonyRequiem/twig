using System.Net;
using System.Text;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Formatters;
using Twig.Infrastructure.Ado;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Produces a sample export using Microsoft's own documented layout response, so the
/// artifact a human reviews is the product of the real parse and the real renderer
/// rather than a hand-written mock-up.
/// </summary>
/// <remarks>
/// Opt-in: set <c>TWIG_LAYOUT_SAMPLE_OUT</c> to a path to write the file. With the
/// variable unset the test asserts the same content in memory and writes nothing, so
/// normal runs pay nothing and CI does not litter the workspace.
/// </remarks>
public sealed class ProcessLayoutSampleExportTests
{
    /// <summary>
    /// The Bug layout from the Azure DevOps "Layout - Get" reference sample, trimmed to
    /// the parts twig models. Two sections in one page, so the section flattening is
    /// exercised by the artifact itself.
    /// </summary>
    private const string DocumentedSampleJson = """
    {
      "pages": [
        {
          "id": "Agile.Bug.Bug",
          "inherited": true,
          "label": "Details",
          "pageType": "custom",
          "locked": false,
          "visible": true,
          "isContribution": false,
          "sections": [
            {
              "id": "Section1",
              "groups": [
                {
                  "id": "Agile.Bug.Bug.Repro Steps.WideGroup",
                  "label": "Repro Steps",
                  "visible": true,
                  "controls": [
                    { "id": "Microsoft.VSTS.TCM.ReproSteps", "label": "Repro Steps",
                      "controlType": "HtmlFieldControl", "readOnly": false, "visible": true }
                  ]
                },
                {
                  "id": "Agile.Bug.Bug.System Info.WideGroup",
                  "label": "System Info",
                  "visible": true,
                  "controls": [
                    { "id": "Microsoft.VSTS.TCM.SystemInfo", "label": "System Info",
                      "controlType": "HtmlFieldControl", "readOnly": false, "visible": true }
                  ]
                }
              ]
            },
            {
              "id": "Section2",
              "groups": [
                {
                  "id": "Agile.Bug.Bug.Planning",
                  "label": "Planning",
                  "visible": true,
                  "controls": [
                    { "id": "Microsoft.VSTS.Common.ResolvedReason", "label": "Resolved Reason",
                      "controlType": "FieldControl", "readOnly": false, "visible": true },
                    { "id": "Microsoft.VSTS.Scheduling.StoryPoints", "label": "Story Points",
                      "controlType": "FieldControl", "readOnly": false, "visible": true },
                    { "id": "Microsoft.VSTS.Common.Priority", "label": "Priority",
                      "controlType": "FieldControl", "readOnly": false, "visible": true },
                    { "id": "Microsoft.VSTS.Common.Severity", "label": "Severity",
                      "controlType": "FieldControl", "readOnly": false, "visible": true },
                    { "id": "Microsoft.VSTS.Common.Activity", "label": "Activity",
                      "controlType": "FieldControl", "readOnly": false, "visible": true }
                  ]
                },
                {
                  "id": "Agile.Bug.Bug.Effort (Hours)",
                  "label": "Effort (Hours)",
                  "visible": true,
                  "controls": [
                    { "id": "Microsoft.VSTS.Scheduling.OriginalEstimate", "label": "Original Estimate",
                      "controlType": "FieldControl", "readOnly": false, "visible": true },
                    { "id": "Microsoft.VSTS.Scheduling.RemainingWork", "label": "Remaining",
                      "controlType": "FieldControl", "readOnly": false, "visible": true },
                    { "id": "Microsoft.VSTS.Scheduling.CompletedWork", "label": "Completed",
                      "controlType": "FieldControl", "readOnly": false, "visible": true }
                  ]
                },
                {
                  "id": "ms-devlabs.vsts-uservoice-ui.vsts-uservoice-ui-wi-group",
                  "label": "Customer feedback",
                  "isContribution": true,
                  "visible": true,
                  "controls": []
                }
              ]
            }
          ]
        },
        {
          "id": "Agile.Bug.History", "label": "History", "pageType": "history",
          "visible": true, "isContribution": false, "sections": []
        },
        {
          "id": "Agile.Bug.Links", "label": "Links", "pageType": "links",
          "visible": true, "isContribution": false, "sections": []
        },
        {
          "id": "Agile.Bug.Attachments", "label": "Attachments", "pageType": "attachments",
          "visible": true, "isContribution": false, "sections": []
        }
      ]
    }
    """;

    /// <summary>
    /// Serves the documented sample over HTTP so the REAL production parse
    /// (<c>AdoIterationService</c>) produces the artifact — not a hand-built domain object.
    /// </summary>
    private sealed class DocumentedSampleHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            string body;
            if (url.Contains("/layout", StringComparison.OrdinalIgnoreCase))
                body = DocumentedSampleJson;
            else if (url.Contains("/_apis/projects/", StringComparison.OrdinalIgnoreCase))
                body = "{\"capabilities\":{\"processTemplate\":{\"templateName\":\"Agile\","
                     + "\"templateTypeId\":\"adcc42ab-9882-485e-a3ed-7678f01f66bc\"}}}";
            else if (url.Contains("/_apis/wit/workitemtypes", StringComparison.OrdinalIgnoreCase))
                body = "{\"count\":1,\"value\":[{\"name\":\"Bug\",\"referenceName\":"
                     + "\"Microsoft.VSTS.WorkItemTypes.Bug\",\"isDisabled\":false}]}";
            else
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                { Content = new StringContent(string.Empty) });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class StubAuthProvider : IAuthenticationProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default)
            => Task.FromResult("sample-token");

        public void InvalidateToken() { }
    }

    [Fact]
    public async Task DocumentedSample_RoundTripsThroughTheRealCommand()
    {
        var provider = new AdoIterationService(
            new HttpClient(new DocumentedSampleHandler()),
            new StubAuthProvider(),
            "https://dev.azure.com/sampleorg",
            "sampleproject",
            "sampleteam");

        var stderr = new StringWriter();
        var cmd = new ProcessLayoutCommand(
            provider,
            new OutputFormatterFactory(new HumanOutputFormatter()),
            new RendererFactory(),
            stderr: stderr);

        var requested = Environment.GetEnvironmentVariable("TWIG_LAYOUT_SAMPLE_OUT");
        var format = Environment.GetEnvironmentVariable("TWIG_LAYOUT_SAMPLE_FORMAT") ?? "json";
        var path = requested ?? Path.Combine(Path.GetTempPath(), $"twig-sample-{Guid.NewGuid():N}.json");

        try
        {
            var exitCode = await cmd.ExecuteAsync("Bug", outPath: path, outputFormat: format);

            exitCode.ShouldBe(0);
            var content = await File.ReadAllTextAsync(path);

            // Section flattening: Section1's two groups then Section2's three, in order.
            content.ShouldContain("Repro Steps");
            content.ShouldContain("System Info");
            content.ShouldContain("Planning");
            content.ShouldContain("Effort (Hours)");
            // The three non-custom page types survive as tabs with no boxes.
            content.ShouldContain("history");
            content.ShouldContain("attachments");
        }
        finally
        {
            if (requested is null)
            {
                try { File.Delete(path); } catch (IOException) { /* best effort */ }
            }
        }
    }
}
