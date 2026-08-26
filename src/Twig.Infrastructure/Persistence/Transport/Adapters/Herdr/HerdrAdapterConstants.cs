namespace Twig.Infrastructure.Persistence.Transport.Adapters.Herdr;

/// <summary>
/// Contract §7.4 / §12.2 fixed vocabulary for the Herdr adapter. Every
/// literal below is a stable wire string; changing one is a schema
/// change to <c>docs/projects/transport-attachment.design.md</c>.
/// </summary>
internal static class HerdrAdapterConstants
{
    /// <summary>§7.2 / §12.2 — the registration key. Lowercase, opaque,
    /// selected by string equality (§7.2). Never routed on any other
    /// axis.</summary>
    public const string AdapterId = "herdr";

    /// <summary>§7.1 — human-facing diagnostic name; NEVER used as a
    /// router key.</summary>
    public const string DisplayName = "Herdr";

    /// <summary>§7.1 — opaque semver-shaped adapter version. Bumped on
    /// any implementation change a caller could observe.</summary>
    public const string AdapterVersion = "1.0.0";

    // §7.4 — the three <c>hostAttachmentIdKind</c> tokens the Herdr
    // adapter mints. Fixed by §12.2's "MUST populate adapterContext with
    // at least workspace and, when applicable, tab and pane" rule.

    /// <summary>§7.4 — <c>hostAttachmentId</c> is a Herdr workspace id
    /// (e.g. <c>"w3"</c>).</summary>
    public const string HostAttachmentIdKindWorkspace = "herdr-workspace";

    /// <summary>§7.4 — <c>hostAttachmentId</c> is a Herdr tab id
    /// (e.g. <c>"w3:t1"</c>).</summary>
    public const string HostAttachmentIdKindTab = "herdr-tab";

    /// <summary>§7.4 — <c>hostAttachmentId</c> is a Herdr pane id
    /// (e.g. <c>"w3:p1"</c>). Moved panes get a NEW id; a cached id is
    /// not authoritative — §12.2's preflight cross-check owns the
    /// re-verification.</summary>
    public const string HostAttachmentIdKindPane = "herdr-pane";
}

/// <summary>
/// Contract §7.4 <c>adapterContext</c> keys the Herdr adapter mints and
/// consumes. §12.2 mandates <see cref="Workspace"/> and, when
/// applicable, <see cref="Tab"/> and <see cref="Pane"/>. The workspace
/// entry is the "workspace-qualified" hook every §5 poll and §6 close
/// path routes through.
/// </summary>
internal static class HerdrAdapterContextKeys
{
    /// <summary>§7.4 — Herdr workspace id (e.g. <c>"w3"</c>).</summary>
    public const string Workspace = "workspace";

    /// <summary>§7.4 — Herdr tab id (e.g. <c>"w3:t1"</c>). Present when
    /// the target is a tab or a pane inside a tab.</summary>
    public const string Tab = "tab";

    /// <summary>§7.4 — Herdr pane id (e.g. <c>"w3:p1"</c>). Present when
    /// the target is a pane.</summary>
    public const string Pane = "pane";

    /// <summary>§12.2 — the target string passed to
    /// <c>herdr agent explain &lt;target&gt; --json</c> and
    /// <c>herdr agent wait &lt;target&gt; --until &lt;state&gt; --timeout &lt;ms&gt;</c>.
    /// Adapter-defined key; may be absent for non-agent targets.
    /// </summary>
    public const string AgentTarget = "agentTarget";
}
