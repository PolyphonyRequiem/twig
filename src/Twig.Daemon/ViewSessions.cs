using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bench.Domain;
using Bench.Infrastructure;

namespace Bench.Server;

public sealed class HttpFailure(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public sealed class ViewSession(Guid id, Guid catalogId, bool canvas, string presentationHash, DateTimeOffset expires)
{
    internal Guid Id { get; } = id;
    internal Guid CatalogId { get; } = catalogId;
    internal bool Canvas { get; } = canvas;
    internal bool OwnerAuthenticated { get; set; }
    internal bool Revoked { get; set; }
    internal Guid ProfileGeneration { get; set; }
    internal SemaphoreSlim RecoveryGate { get; } = new(1, 1);
    internal bool ReferencesRestored { get; set; }
    internal NativeViewChoices? AppliedChoices { get; set; }
    internal Dictionary<string, NativeSourceRecovery> SourceRecovery { get; } = new(PathComparer);
    private Guid? currentBenchId;
    internal long SelectionRevision { get; private set; }
    internal long ExplicitSelectionRevision { get; private set; }
    internal void AdvanceSelectionRevision()
    {
        SelectionRevision++;
        ExplicitSelectionRevision++;
    }
    internal bool StartupSelectionResolved { get; set; }
    internal Guid? CurrentBenchId
    {
        get => currentBenchId;
        set
        {
            if (currentBenchId == value) return;
            currentBenchId = value;
            SelectionRevision++;
        }
    }
    internal string? RepositoryRoot { get; set; }
    internal Twig.Infrastructure.Services.UserRepositoryBindings? RepositoryBindings { get; set; }
    internal CanvasRuntimeContext? RuntimeContext { get; set; }
    internal bool RuntimeRepositoryApplied { get; set; }
    internal string? RuntimeRepositoryRoot { get; set; }
    internal bool RepositoryContextOverridden { get; set; }
    internal SemaphoreSlim RepositoryGate { get; } = new(1, 1);
    internal static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    internal Bench.Infrastructure.NativePanelContext? NativeContext { get; set; }
    internal Bench.Infrastructure.NativePanelContext? PopulationContext { get; set; }
    internal Dictionary<Twig.Core.Connection, Bench.Infrastructure.NativePanelContext> DisplayContexts { get; } = [];
    internal bool NativeContentBlocked => Canvas && !OwnerAuthenticated;
    internal string PresentationHash { get; } = presentationHash;
    internal DateTimeOffset Expires { get; set; } = expires;
    internal DateTimeOffset PresentationExpires { get; } = expires;
    internal string? ContentHash { get; set; }
    internal bool Attached { get; set; }
    internal Guid ProfileId { get; set; }
    internal ViewPreferences Preferences { get; set; } = new(0, null, "list", null, null);
    internal NativeEditorView Editor { get; } = new();
    internal NativePlanView Plan { get; } = new();
    internal NativeSetupView Setup { get; } = new();
    internal NativeAgentView Agent { get; } = new();
}

public sealed partial class ViewSessions(string preferencesDirectory, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan OwnerLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan OwnerRenewalLead = TimeSpan.FromHours(12);
    private readonly object gate = new();
    private readonly Dictionary<Guid, ViewSession> sessions = [];
    public CanvasRegistration RegisterNativeCanvas(Guid instance, CanvasRuntimeContext? context = null)
    {
        lock (gate)
        {
            if (context is not null && (!ValidText(context.SessionId, 256)
                || new[] { context.WorkingDirectory, context.GitRoot }.Any(value => value is not null && !ValidText(value, 4096))
                || new[] { context.Repository, context.Branch }.Any(value => value is not null && !ValidText(value, 512))))
                throw new HttpFailure(400, "InvalidInput", "Use bounded SDK session context without credentials or conversation content.");
            if (context?.IsRemote == true)
                throw new HttpFailure(422, "RemoteContextUnsupported", "This daemon supports same-machine session contexts only. No remote directory was mapped to local data.");
            var registered = Register(instance, true);
            registered.Session.RuntimeContext = context;
            return new(registered.Session.Id, registered.Secret);
        }
    }

    public OwnerProfileInfo ResolveOwnerProfile(Guid instance, OwnerProfileBody body)
    {
        lock (gate)
        {
            if (body.StoreId is { } known && known != instance)
                throw new HttpFailure(409, "StoreReplaced", "The native Bench store changed. Connect explicitly.");
            if (body.ProfileId == Guid.Empty || body.StoreId == Guid.Empty)
                throw new HttpFailure(400, "InvalidInput", "Use an exact remembered store/profile or no remembered identity.");
            if (body.ViewId is { } viewId)
            {
                if (!sessions.TryGetValue(viewId, out var view) || view.CatalogId != instance || view.Revoked
                    || view.Expires <= time.GetUtcNow() || !view.Canvas)
                    throw new HttpFailure(409, "ViewUnavailable", "Open the current Canvas view before resolving its session profile.");
                if (view.RuntimeContext is { } runtime)
                {
                    var contextual = ContextualProfile(instance, runtime);
                    var contextualProbe = new ViewSession(Guid.NewGuid(), instance, false, "", time.GetUtcNow()) { ProfileId = contextual };
                    using var contextualLease = ProfileLease(contextualProbe);
                    if (!File.Exists(NativePath(contextualProbe))) WriteDocument(contextualProbe, ReadDocument(contextualProbe));
                    return new(instance, contextual);
                }
            }
            var path = DefaultProfilePath(instance);
            Guid? selected = null;
            if (body.StoreId == instance && body.ProfileId is { } prior)
            {
                var previous = new ViewSession(Guid.NewGuid(), instance, false, "", time.GetUtcNow()) { ProfileId = prior };
                using var lease = ProfileLease(previous);
                if (File.Exists(NativePath(previous)) && ReadDocument(previous).SignedOut)
                    return new(instance, prior);
                if (!File.Exists(path) && File.Exists(NativePath(previous))) selected = prior;
            }
            if (selected is null && File.Exists(path))
            {
                if (new FileInfo(path).Length != 36 || !Guid.TryParseExact(File.ReadAllText(path), "D", out var retained)
                    || retained == Guid.Empty)
                    throw new InvalidDataException("The retained owner profile identity is invalid.");
                selected = retained;
                var retainedSession = new ViewSession(Guid.NewGuid(), instance, false, "", time.GetUtcNow()) { ProfileId = retained };
                if (!File.Exists(NativePath(retainedSession)))
                    throw new InvalidDataException("The retained owner profile is unavailable.");
            }
            var profile = selected ?? Guid.NewGuid();
            var probe = new ViewSession(Guid.NewGuid(), instance, false, "", time.GetUtcNow()) { ProfileId = profile };
            using (var lease = ProfileLease(probe))
            {
                var document = ReadDocument(probe);
                if (!File.Exists(NativePath(probe))) WriteDocument(probe, document);
            }
            if (!File.Exists(path)) WriteAtomic(path, profile.ToString("D"));
            return new(instance, profile);
        }
    }

    // Called only by the same-Windows-owner authenticated HTTP endpoint, never by the SDK adapter.
    public PairResult ConnectOwner(Guid instance, Guid? viewId, Guid profileId)
        => ConnectOwnerCore(instance, viewId, profileId, explicitConnect: true);

    // The Windows-owner endpoint keeps the generated human capability inside the daemon.
    public (ViewSession Session, bool Restore) AuthenticateAutomaticOwner(Guid instance, Guid viewId)
    {
        lock (gate)
        {
            if (!sessions.TryGetValue(viewId, out var session) || !session.Canvas
                || session.CatalogId != instance || session.Revoked || session.Expires <= time.GetUtcNow())
                throw new HttpFailure(403, "CapabilityRejected", "Open a fresh panel view.");
            var restore = !session.OwnerAuthenticated;
            if (!restore) _ = ReadState(session);
            else
            {
                var profile = ResolveOwnerProfile(instance, new(null, null, viewId));
                _ = RenewOwner(instance, new(viewId, profile.ProfileId, instance));
            }
            session.Agent.OwnerHostAuthenticated = true;
            return (session, restore);
        }
    }

    private PairResult ConnectOwnerCore(Guid instance, Guid? viewId, Guid profileId, bool explicitConnect, Guid? generation = null)
    {
        lock (gate)
        {
            if (profileId == Guid.Empty) throw new HttpFailure(400, "InvalidInput", "A presentation profile is required.");
            Prune();
            ViewSession session;
            if (viewId is { } requested)
            {
                if (!sessions.TryGetValue(requested, out session!) || session.CatalogId != instance || !session.Canvas)
                    throw new HttpFailure(403, "CapabilityRejected", "Open a fresh panel view.");
            }
            else session = Register(instance, false).Session;
            if (session.RuntimeContext is { } runtime && profileId != ContextualProfile(instance, runtime))
                throw new HttpFailure(409, "SessionProfileChanged", "Use the profile belonging to this SDK session; no other session's selections were adopted.");
            if (session.OwnerAuthenticated && session.ProfileId != profileId)
                throw new HttpFailure(409, "ProfileChanged", "Open a fresh view for the selected presentation profile.");
            session.OwnerAuthenticated = true;
            session.ProfileId = profileId;
            using (var lease = ProfileLease(session))
            {
                var document = ReadDocument(session);
                if (!explicitConnect && (document.SignedOut || generation is { } expected && document.Generation != expected))
                    throw new HttpFailure(403, "SessionRevoked", "This profile was signed out. Connect explicitly to use it again.");
                if (document.SignedOut)
                {
                    document = document with { SignedOut = false, Revision = checked(document.Revision + 1) };
                    WriteDocument(session, document);
                }
                else if (!File.Exists(NativePath(session))) WriteDocument(session, document);
                session.ProfileGeneration = document.Generation;
                ApplyPreferences(session, document);
                session.AppliedChoices = document.Choices;
            }
            session.Revoked = false;
            session.Expires = time.GetUtcNow() + OwnerLifetime;
            var content = Secret();
            session.ContentHash = Hash(content);
            return new(session.Id, content, false);
        }
    }
    public ViewSession Content(Guid id, string capability, bool native)
    {
        lock (gate)
        {
            var session = Admit(id, capability, false);
            if (native && session.NativeContentBlocked)
                throw new HttpFailure(403, "CanvasDisclosureBlocked", "Authenticate this canvas with its Windows owner before reading native content.");
            return session;
        }
    }
    public ViewSession Presentation(Guid id, string capability)
    {
        lock (gate) return Admit(id, capability, true);
    }
    public void AttachPresentation(Guid id, string capability)
    {
        lock (gate)
        {
            var session = Admit(id, capability, false);
            if (!session.Canvas) throw new HttpFailure(403, "CanvasRequired", "Presentation attachment belongs to a canvas view.");
            session.Attached = true;
        }
    }
    public void DetachPresentation(ViewSession session)
    {
        lock (gate) session.Attached = false;
    }
    public void SetLayout(Guid id, string capability, LayoutBody body)
    {
        lock (gate)
        {
            var session = Admit(id, capability, true);
            if (!session.Attached) throw new HttpFailure(403, "NotAttached", "Connect this canvas and explicitly attach layout controls.");
            if (session.OwnerAuthenticated) _ = ReadState(session);
            Update(session, session.Preferences with { Layout = body.Layout });
        }
    }
    public void Update(ViewSession session, ViewPreferences value, long? expectedRevision = null)
    {
        lock (gate)
        {
            if (session.OwnerAuthenticated)
            {
                using var lease = ProfileLease(session);
                var document = ReadDocument(session);
                RequireProfile(session, document);
                if ((expectedRevision ?? value.Revision) != document.Revision)
                    throw StateConflict();
                ValidatePreferences(value);
                var choices = document.Choices with
                { Layout = value.Layout, GroupSource = value.GroupSource, GroupField = value.GroupField };
                document = document with { Revision = checked(document.Revision + 1), Choices = choices };
                WriteDocument(session, document);
                ApplyPreferences(session, document);
                return;
            }
            if (expectedRevision is { } expected && session.Preferences.Revision != expected)
                throw new HttpFailure(409, "Conflict", "View changed in another request. Re-read before changing it.");
            if (value.BenchId is not null || value.GroupSource is not null || value.GroupField is not null)
                throw new HttpFailure(403, "CanvasDisclosureBlocked", "This canvas permits layout only.");
            if (value.Layout is not ("tree" or "list") || value.GroupField is { } field
                && (field.Length is < 1 or > 256 || field.Any(char.IsControl))
                || value.BenchId == Guid.Empty || value.GroupSource == Guid.Empty
                || (value.GroupSource is null) != (value.GroupField is null))
                throw new HttpFailure(400, "InvalidInput", "Invalid view preferences.");
            var next = value with { Revision = checked(session.Preferences.Revision + 1) };
            Directory.CreateDirectory(preferencesDirectory);
            var path = PreferencePath(session);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(next, TransportJson.Default.ViewPreferences));
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            session.Preferences = next;
        }
    }
    public void Revoke(ViewSession session)
    {
        lock (gate)
        {
            if (session.OwnerAuthenticated)
            {
                using var lease = ProfileLease(session);
                var document = ReadDocument(session);
                document = document with { SignedOut = true, Generation = Guid.NewGuid(), Revision = checked(document.Revision + 1) };
                WriteDocument(session, document);
                foreach (var other in sessions.Values.Where(value => value.OwnerAuthenticated
                    && value.CatalogId == session.CatalogId && value.ProfileId == session.ProfileId))
                    Retire(other);
            }
            else Retire(session);
        }
    }
    public void Close(Guid id, string capability)
    {
        lock (gate) { var session = Admit(id, capability, true); session.Agent.Invalidate(); session.Editor.Invalidate(retire: true); session.Plan.Invalidate(retire: true); session.Setup.Invalidate(); sessions.Remove(id); }
    }
    private string PreferencePath(ViewSession session)
        => Path.Combine(preferencesDirectory, $"{session.CatalogId:D}-{session.ProfileId:D}-{(session.Canvas ? "canvas" : "browser")}.json");
    private (ViewSession Session, string Secret) Register(Guid catalogId, bool canvas)
    {
        Prune();
        if (sessions.Count >= 64) throw new HttpFailure(429, "ViewLimit", "Close old views or wait for expiry.");
        var token = Secret();
        var session = new ViewSession(Guid.NewGuid(), catalogId, canvas, Hash(token), time.GetUtcNow() + Lifetime);
        sessions.Add(session.Id, session);
        return (session, token);
    }
    private ViewSession Admit(Guid id, string token, bool presentation)
    {
        if (!ValidSecret(token) || !sessions.TryGetValue(id, out var value)
            || !Equal(Hash(token), presentation ? value.PresentationHash : value.ContentHash))
            throw new HttpFailure(403, "CapabilityRejected", "Expired, revoked, replaced or wrong-scope capability. Reconnect this view.");
        if (value.Revoked)
            throw new HttpFailure(403, "SessionRevoked", "This view was signed out. Connect explicitly to use it again.");
        if ((presentation && !value.OwnerAuthenticated ? value.PresentationExpires : value.Expires) <= time.GetUtcNow())
            throw new HttpFailure(403, "SessionExpired", "Renew this view with the same Windows owner before continuing.");
        if (!presentation && value.OwnerAuthenticated)
        {
            using var lease = ProfileLease(value);
            var document = ReadDocument(value);
            RequireProfile(value, document);
            ApplyPreferences(value, document);
        }
        return value;
    }
    private void Prune()
    {
        var now = time.GetUtcNow();
        foreach (var id in sessions.Where(s => s.Value.Expires <= now).Select(s => s.Key).ToArray())
        { sessions[id].Editor.Invalidate(retire: true); sessions[id].Plan.Invalidate(retire: true); sessions[id].Setup.Invalidate(); sessions.Remove(id); }
    }
    public (PairResult Connection, bool Recovered) RenewOwner(Guid instance, OwnerRenewBody body, string? contentCapability = null)
    {
        lock (gate)
        {
            if (body.ProfileId == Guid.Empty)
                throw new HttpFailure(400, "InvalidInput", "A presentation profile is required.");
            if (body.StoreId is { } store && store != instance)
                throw new HttpFailure(409, "StoreReplaced", "The native Bench store changed. Connect explicitly.");
            sessions.TryGetValue(body.ViewId ?? Guid.Empty, out var existing);
            if (existing is not null && existing.CatalogId != instance)
                throw new HttpFailure(409, "StoreReplaced", "The native Bench store changed. Connect explicitly.");
            if (existing is { OwnerAuthenticated: true } && existing.ProfileId != body.ProfileId)
                throw new HttpFailure(409, "ProfileChanged", "The view belongs to another presentation profile.");
            var probe = existing ?? new ViewSession(Guid.NewGuid(), instance, false, "", time.GetUtcNow());
            probe.ProfileId = body.ProfileId;
            using (var lease = ProfileLease(probe))
            {
                var document = ReadDocument(probe);
                if (document.SignedOut || existing?.Revoked == true
                    || existing is { OwnerAuthenticated: true } && existing.ProfileGeneration != document.Generation)
                    throw new HttpFailure(403, "SessionRevoked", "This profile was signed out. Connect explicitly to use it again.");
            }
            var recovered = existing is null || existing.Expires <= time.GetUtcNow() || !existing.OwnerAuthenticated;
            if (recovered)
            {
                if (existing is { Canvas: true, OwnerAuthenticated: false } && existing.Expires > time.GetUtcNow())
                    return (ConnectOwnerCore(instance, existing.Id, body.ProfileId, explicitConnect: false), true);
                if (existing is not null) { Retire(existing); sessions.Remove(existing.Id); }
                var connection = ConnectOwnerCore(instance, null, body.ProfileId, explicitConnect: false,
                    existing is { OwnerAuthenticated: true } ? existing.ProfileGeneration : null);
                return (connection, true);
            }
            existing!.Expires = time.GetUtcNow() + OwnerLifetime;
            var secret = ValidSecret(contentCapability) && Equal(Hash(contentCapability!), existing.ContentHash)
                ? contentCapability! : Secret();
            existing.ContentHash = Hash(secret);
            return (new(existing.Id, secret, false), false);
        }
    }

    public NativeViewStateInfo ReadState(ViewSession session)
    {
        lock (gate)
        {
            RequireOwner(session);
            using var lease = ProfileLease(session);
            var document = ReadDocument(session);
            RequireProfile(session, document);
            ApplyPreferences(session, document);
            return StateInfo(session, document);
        }
    }

    public NativeViewStateInfo SaveState(ViewSession session, NativeViewStateBody body)
        => SaveState(session, body, null);

    private NativeViewStateInfo SaveState(ViewSession session, NativeViewStateBody body, NativePanelContext[]? exactContexts)
    {
        lock (gate)
        {
            RequireOwner(session);
            ValidateChoices(body.Choices);
            using var lease = ProfileLease(session);
            var document = ReadDocument(session);
            RequireProfile(session, document);
            if (document.Revision != body.ExpectedRevision) throw StateConflict();
            NativePanelContext[] contexts;
            lock (session) contexts = exactContexts ?? session.DisplayContexts.Values
                .Concat(new[] { session.NativeContext, session.PopulationContext }.OfType<NativePanelContext>()).ToArray();
            var references = Roots(body.Choices).Select(root =>
            {
                var context = contexts.FirstOrDefault(value => ViewSession.PathComparer.Equals(value.Root, root));
                var prior = document.Sources.FirstOrDefault(value => ViewSession.PathComparer.Equals(value.Root, root));
                return context is not null ? new NativeSourceReference(root, CatalogFingerprint.Binding(context.Source.Binding))
                    : prior
                    ?? throw new HttpFailure(409, "ContextRequired", "Choose each intended connection explicitly before saving its reference.");
            }).ToArray();
            document = document with { Revision = checked(document.Revision + 1), Choices = body.Choices, Sources = references };
            WriteDocument(session, document);
            if (session.RuntimeContext is null)
                WriteAtomic(DefaultProfilePath(session.CatalogId), session.ProfileId.ToString("D"));
            lock (session)
                foreach (var reference in references)
                    if (contexts.Any(context => ViewSession.PathComparer.Equals(context.Root, reference.Root)))
                        session.SourceRecovery[reference.Root] = new(reference.Root, "retained", null);
            ApplyPreferences(session, document);
            return StateInfo(session, document);
        }
    }

    internal NativeSourceReference[] StateReferences(ViewSession session)
    {
        lock (gate)
        {
            RequireOwner(session);
            using var lease = ProfileLease(session);
            var document = ReadDocument(session);
            RequireProfile(session, document);
            return document.Sources;
        }
    }

    internal Guid? ReadRepositoryBench(ViewSession session, string repositoryId)
    {
        lock (gate)
        {
            RequireOwner(session);
            var path = RepositoryBenchPath(session.CatalogId, repositoryId);
            FileStream stream;
            try { stream = File.OpenRead(path); }
            catch (FileNotFoundException) { return null; }
            using var reader = new StreamReader(stream);
            if (stream.Length != 36 || !Guid.TryParseExact(reader.ReadToEnd(), "D", out var bench)
                || bench == Guid.Empty)
                throw new InvalidDataException("The retained repository Bench selection is invalid.");
            return bench;
        }
    }

    internal void CommitSelection(ViewSession session, NativeViewStateInfo before, Guid? bench,
        NativePanelContext? viewing, NativePanelContext? add, NativePanelContext[] display, Action validate)
    {
        lock (gate)
        lock (session)
        {
            RequireOwner(session);
            validate();
            var saved = SaveState(session, new(before.Revision, before.Choices with
            {
                RepositoryRoot = session.RepositoryRoot, BenchId = bench,
                ViewingRoot = viewing?.Root, AddRoot = add?.Root,
                DisplayRoots = [], GroupSource = null, GroupField = null,
            }), new[] { viewing, add }.OfType<NativePanelContext>().ToArray());
            if (session.RepositoryBindings is { } repository)
            {
                var path = RepositoryBenchPath(session.CatalogId, repository.RepositoryId);
                if (bench is { } selected) WriteAtomic(path, selected.ToString("D"));
                else File.Delete(path);
            }
            session.AppliedChoices = saved.Choices;
            session.CurrentBenchId = bench;
            session.AdvanceSelectionRevision();
            session.StartupSelectionResolved = true;
            session.ReferencesRestored = true;
            session.NativeContext = viewing ?? (display.Length == 1 ? display[0] : null);
            session.PopulationContext = add;
            session.DisplayContexts.Clear();
            foreach (var source in display) session.DisplayContexts[source.Stamp.Connection] = source;
        }
    }

    internal NativeViewStateInfo CommitRestoration(ViewSession session, long revision, Action apply)
    {
        lock (gate)
        {
            RequireOwner(session);
            using var lease = ProfileLease(session);
            var document = ReadDocument(session);
            RequireProfile(session, document);
            if (document.Revision != revision) throw StateConflict();
            lock (session)
            {
                apply();
                return StateInfo(session, document);
            }
        }
    }

    private NativeViewStateInfo StateInfo(ViewSession session, NativeViewDocument document)
    {
        NativeSourceRecovery[] recovery;
        lock (session)
        {
            var retained = session.DisplayContexts.Values.Concat(
                new[] { session.NativeContext, session.PopulationContext }.OfType<NativePanelContext>()).Select(context => context.Root)
                .ToHashSet(ViewSession.PathComparer);
            recovery = document.Sources.Select(reference => session.SourceRecovery.GetValueOrDefault(reference.Root)
                ?? new(reference.Root, retained.Contains(reference.Root) ? "retained" : "not-opened", null)).ToArray();
        }
        var choices = document.Choices;
        if (session.RuntimeRepositoryApplied && session.RuntimeContext is not null && !session.RepositoryContextOverridden
            && !ViewSession.PathComparer.Equals(choices.RepositoryRoot, session.RuntimeRepositoryRoot))
            choices = choices with { RepositoryRoot = session.RuntimeRepositoryRoot, BenchId = null,
                ViewingRoot = null, AddRoot = null, DisplayRoots = [], GroupSource = null, GroupField = null };
        return new(session.CatalogId, session.ProfileId, document.Revision, choices, recovery,
            session.CurrentBenchId, session.Expires, session.Expires - OwnerRenewalLead) { RuntimeContext = session.RuntimeContext };
    }
    internal static string[] Roots(NativeViewChoices choices) => new[] { choices.ViewingRoot, choices.AddRoot }
        .OfType<string>().Concat(choices.DisplayRoots).Distinct(ViewSession.PathComparer).ToArray();
    private static void ApplyPreferences(ViewSession session, NativeViewDocument document)
        => session.Preferences = new(document.Revision, document.Choices.BenchId, document.Choices.Layout,
            document.Choices.GroupSource, document.Choices.GroupField);
    private static void Retire(ViewSession session)
    {
        session.Agent.Invalidate();
        session.Editor.Invalidate(retire: true);
        session.Plan.Invalidate(retire: true);
        session.Setup.Invalidate();
        session.Revoked = session.OwnerAuthenticated;
        session.ContentHash = null;
        session.Attached = false;
        session.NativeContext = null;
        session.PopulationContext = null;
        lock (session) { session.DisplayContexts.Clear(); session.SourceRecovery.Clear(); }
    }
    private static void RequireOwner(ViewSession session)
    {
        if (!session.OwnerAuthenticated || session.Revoked)
            throw new HttpFailure(403, "OwnerRequired", "Only an authenticated human view can retain or restore native choices.");
    }
    private static void RequireProfile(ViewSession session, NativeViewDocument document)
    {
        if (session.Revoked || document.SignedOut || session.ProfileGeneration != document.Generation)
            throw new HttpFailure(403, "SessionRevoked", "This profile was signed out. Connect explicitly to use it again.");
    }
    private static HttpFailure StateConflict()
        => new(409, "ViewStateConflict", "The saved profile changed in another view. Read its current revision and reconcile before saving.");
    private string NativePath(ViewSession session)
        => Path.Combine(preferencesDirectory, $"native-{session.CatalogId:D}-{session.ProfileId:D}.json");
    private string DefaultProfilePath(Guid instance)
        => Path.Combine(preferencesDirectory, $"default-profile-{instance:D}");
    private string RepositoryBenchPath(Guid instance, string repositoryId)
        => Path.Combine(preferencesDirectory, $"repository-bench-{instance:D}-{Hash(repositoryId)}");
    private FileStream ProfileLease(ViewSession session)
    {
        Directory.CreateDirectory(preferencesDirectory);
        try { return new FileStream(NativePath(session) + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 33)
        { throw new HttpFailure(409, "ViewStateBusy", "Another request is updating this profile. Read it again before saving."); }
    }
    private NativeViewDocument ReadDocument(ViewSession session)
    {
        var path = NativePath(session);
        if (!File.Exists(path))
            return new(1, session.CatalogId, session.ProfileId, 0, Guid.NewGuid(), false, NativeViewChoices.Empty, []);
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Native view state exceeds its bound.");
        var document = JsonSerializer.Deserialize(File.ReadAllText(path), TransportJson.Default.NativeViewDocument)
            ?? throw new InvalidDataException("Native view state is invalid.");
        if (document.Version != 1 || document.StoreId != session.CatalogId || document.ProfileId != session.ProfileId
            || document.Revision < 0 || document.Generation == Guid.Empty || document.Sources.Length > 64
            || document.Sources.Any(source => !ValidDigest(source.Binding))
            || !Roots(document.Choices).Order(ViewSession.PathComparer)
                .SequenceEqual(document.Sources.Select(source => source.Root).Order(ViewSession.PathComparer), ViewSession.PathComparer))
            throw new InvalidDataException("Native view state identity is invalid.");
        ValidateChoices(document.Choices);
        return document;
    }
    private void WriteDocument(ViewSession session, NativeViewDocument document)
    {
        var json = JsonSerializer.Serialize(document, TransportJson.Default.NativeViewDocument);
        if (Encoding.UTF8.GetByteCount(json) > 65536)
            throw new HttpFailure(400, "InvalidInput", "The selected view references exceed their storage bound.");
        WriteAtomic(NativePath(session), json);
    }
    private static void WriteAtomic(string path, string text)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                file.Write(bytes);
                file.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static bool ValidDigest(string value)
        => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void ValidatePreferences(ViewPreferences value)
    {
        if (value.Layout is not ("tree" or "list") || value.BenchId == Guid.Empty || value.GroupSource == Guid.Empty
            || (value.GroupSource is null) != (value.GroupField is null)
            || value.GroupField is { } field && !ValidText(field, 256))
            throw new HttpFailure(400, "InvalidInput", "Invalid view preferences.");
    }
    private static bool ValidText(string value, int bound)
        => value.Length is > 0 && value.Length <= bound && !value.Any(char.IsControl);
    private static void ValidateChoices(NativeViewChoices choices)
    {
        ValidatePreferences(new(0, choices.BenchId, choices.Layout, choices.GroupSource, choices.GroupField));
        if (choices.DisplayRoots.Length > 64 || Roots(choices).Length > 64
            || choices.DisplayRoots.Distinct(ViewSession.PathComparer).Count() != choices.DisplayRoots.Length
            || Roots(choices).Any(root => !ValidText(root, 4096) || !Path.IsPathFullyQualified(root)
                || !ViewSession.PathComparer.Equals(root, Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))))
            || choices.Mode is not ("items" or "queries" or "backlogs" or "rules")
            || choices.TeamId is { } team && (!Guid.TryParseExact(team, "D", out var id) || id == Guid.Empty || id.ToString("D") != team)
            || choices.QueryId is { } query && !ValidText(query, 512)
            || choices.BacklogId is { } backlog && !ValidText(backlog, 512))
            throw new HttpFailure(400, "InvalidInput", "Invalid native view references or presentation choices.");
        if (choices.RepositoryRoot is { } repository && (!ValidText(repository, 4096) || !Path.IsPathFullyQualified(repository)))
            throw new HttpFailure(400, "InvalidInput", "Choose an absolute repository path.");
    }
    private static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private static Guid ContextualProfile(Guid instance, CanvasRuntimeContext runtime)
        => new(SHA256.HashData(Encoding.UTF8.GetBytes($"twig-canvas-session-v1\n{instance:D}\n{runtime.SessionId}")).AsSpan(0, 16));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool ValidSecret(string? value) => value is { Length: 64 } && value.All(c => c is >= 'a' and <= 'f' or >= '0' and <= '9');
    private static bool Equal(string actual, string? expected) => expected is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expected));
}
