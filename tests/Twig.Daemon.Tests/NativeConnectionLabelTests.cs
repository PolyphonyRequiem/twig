using Bench.Infrastructure;
using Twig.Core;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Services;

namespace Twig.Daemon.Tests.Native;

internal static class NativeConnectionLabelTests
{
    internal static async Task Run(string parent)
    {
        var bound = new Connection(new("ado"), "scope", "project", """["binding","saved-identity","aad","actor"]""");
        var account = new NativeAadEnrollment("saved", "saved-identity", "11111111-2222-4333-8444-555555555555",
            "object", "issuer", "authority", "fixture@example.net");
        Check(NativeConnectionLabels.Account(bound, [account]) == new NativeConnectionAccount("fixture@example.net", "orgid"),
            "A custom-domain work account must use its registered organizational tenant.");
        Check(NativeConnectionLabels.Account(bound, [account with { TenantId = "9188040d-6c67-4c5b-b112-36a304b66dad" }])
            == new NativeConnectionAccount("fixture@example.net", "msa"),
            "The same custom-domain email can be a personal account; only tenant evidence distinguishes it.");
        Check(NativeConnectionLabels.Account(bound, [account with { IdentityId = "another-account" }]) is null
            && NativeConnectionLabels.Account(bound, [account with { TenantId = "" }]) is null
            && NativeConnectionLabels.Account(bound, [account with { AccountName = null }]) is null,
            "An absent exact identity, account name or tenant must not select a different account or invent its type.");
        Check(NativeConnectionLabels.Account(adoConnection("not-json"), [account]) is null
            && NativeConnectionLabels.Account(adoConnection("""["binding",42,"aad","actor"]"""), [account]) is null,
            "Malformed identity metadata must be explicitly unavailable, not shown as an account.");
        Check(NativeConnectionLabels.Account(new(new("github"), "host", "repository",
            """["binding","1","worktree","1","gh","fixture-login","","","actor"]"""), [])
            == new NativeConnectionAccount("fixture-login", "github"), "GitHub uses the pinned account, not the current gh user.");
        static Connection adoConnection(string identity) => new(new("ado"), "scope", "project", identity);

        var root = Path.Combine(parent, "local connection labels");
        Directory.CreateDirectory(root);
        var ado = new Connection(new("ado"), "11111111-2222-4333-8444-555555555555",
            "66666666-7777-4888-8999-aaaaaaaaaaaa", "bbbbbbbb-cccc-4ddd-8eee-ffffffffffff");
        var missing = await NativeConnectionLabels.ReadAsync(root, ado, default);
        Check(!missing.Available && missing.Name == "Azure DevOps connection", "Missing labels must never fall back to IDs.");
        var config = new TwigConfiguration { Provider = "ado", Organization = "Explicit organization", Project = "Explicit project" };
        var path = Path.Combine(root, "twig.json");
        await config.SaveAsync(path);
        var named = await NativeConnectionLabels.ReadAsync(root, ado, default);
        Check(named.Available && named.Name == "Azure DevOps / Explicit organization / Explicit project",
            "ADO names must come from explicit local native configuration.");
        var legacyRoot = Path.Combine(root, "legacy workspace");
        Directory.CreateDirectory(Path.Combine(legacyRoot, ".twig"));
        await config.SaveAsync(Path.Combine(legacyRoot, ".twig", "config"));
        Check((await NativeConnectionLabels.ReadAsync(legacyRoot, ado, default)).Name == named.Name,
            "The native loader must preserve explicit names from existing legacy workspace configuration.");
        config.Project = ado.Destination;
        await config.SaveAsync(path);
        Check(!(await NativeConnectionLabels.ReadAsync(root, ado, default)).Available, "A GUID project is not a human label.");
        config.Project = ado.Destination.Replace("-", "");
        await config.SaveAsync(path);
        Check(!(await NativeConnectionLabels.ReadAsync(root, ado, default)).Available, "Alternate GUID formats are not human labels.");
        config.Project = @"C:\private\workspace";
        await config.SaveAsync(path);
        Check(!(await NativeConnectionLabels.ReadAsync(root, ado, default)).Available, "A path is not a human label.");
        config.Organization = "https://example.test/?credential=not-a-label";
        config.Project = "Explicit project";
        await config.SaveAsync(path);
        Check(!(await NativeConnectionLabels.ReadAsync(root, ado, default)).Available, "URL credentials/query data must not be a label.");
        config.Provider = "github";
        config.GitHub = new() { Host = "github.com", Repository = "explicit-owner/explicit-repository" };
        await config.SaveAsync(path);
        var github = new Connection(new("github"), ado.ResourceScope, ado.Destination, ado.IdentityRef);
        var repository = await NativeConnectionLabels.ReadAsync(root, github, default);
        Check(repository.Available && repository.Name == "GitHub / explicit-owner/explicit-repository",
            "GitHub owner/repository must be explicitly configured, not inferred from IDs.");
        Check(!(await NativeConnectionLabels.ReadAsync(root, ado, default)).Available, "Provider-mismatched names are not substituted.");
        await File.WriteAllTextAsync(path, "{invalid");
        var invalid = await NativeConnectionLabels.ReadAsync(root, github, default);
        Check(!invalid.Available && invalid.Name == "GitHub connection", "Unreadable metadata must explicitly use a generic provider label.");
        await File.WriteAllTextAsync(path, new string(' ', 256 * 1024 + 1));
        Check(!(await NativeConnectionLabels.ReadAsync(root, github, default)).Available, "Oversized label input must not be acquired.");
        Console.WriteLine("PASS bounded local-only native human labels, GUID/path/credential/provider mismatch refusals and explicit generic fallbacks");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
