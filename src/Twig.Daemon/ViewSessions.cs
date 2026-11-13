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

public sealed class ViewSession(Guid id, Guid catalogId, bool canvas, string presentationHash, DateTimeOffset expires,
    CanvasApproval? approval = null)
{
    internal Guid Id { get; } = id;
    internal Guid CatalogId { get; } = catalogId;
    internal bool Canvas { get; } = canvas;
    internal CanvasApproval? Approval { get; } = approval;
    internal bool OwnerAuthenticated { get; set; }
    internal bool Revoked { get; set; }
    internal Guid ProfileGeneration { get; set; }
    internal SemaphoreSlim RecoveryGate { get; } = new(1, 1);
    internal Dictionary<string, NativeSourceRecovery> SourceRecovery { get; } = new(PathComparer);
    internal Guid? CurrentBenchId { get; set; }
    internal static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    internal Bench.Infrastructure.NativePanelContext? NativeContext { get; set; }
    internal Bench.Infrastructure.NativePanelContext? PopulationContext { get; set; }
    internal Dictionary<Twig.Core.Connection, Bench.Infrastructure.NativePanelContext> DisplayContexts { get; } = [];
    internal bool NativeContentBlocked => Canvas && Approval is null && !OwnerAuthenticated;
    internal string PresentationHash { get; } = presentationHash;
    internal DateTimeOffset Expires { get; set; } = expires;
    internal DateTimeOffset PresentationExpires { get; } = expires;
    internal string? ContentHash { get; set; }
    internal bool Attached { get; set; }
    internal Guid? DeviceId { get; set; }
    internal Guid ProfileId { get; set; }
    internal ViewPreferences Preferences { get; set; } = new(0, null, "list", null, null);
    internal CreateBenchCommand? PendingCreation { get; set; }
    internal NativeEditorView Editor { get; } = new();
    internal NativePlanView Plan { get; } = new();
    internal NativeSetupView Setup { get; } = new();
    internal NativeAgentView Agent { get; } = new();
}

public sealed class ViewSessions(string preferencesDirectory, CanvasApproval? canvasApproval = null, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan OwnerLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan OwnerRenewalLead = TimeSpan.FromHours(12);
    private readonly object gate = new();
    private readonly Dictionary<Guid, ViewSession> sessions = [];
    private readonly Dictionary<string, (DateTimeOffset Expires, bool Canvas)> codes = [];
    private readonly Queue<DateTimeOffset> attempts = [];
    public CanvasRegistration RegisterNativeCanvas(Guid instance)
    {
        lock (gate)
        {
            var registered = Register(instance, true);
            return new(registered.Session.Id, registered.Secret);
        }
    }

    // Called only by the same-Windows-owner authenticated HTTP endpoint, never by the SDK adapter.
    public PairResult ConnectOwner(Guid instance, Guid? viewId, Guid profileId)
        => ConnectOwnerCore(instance, viewId, profileId, explicitConnect: true);

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
            }
            session.Revoked = false;
            session.Expires = time.GetUtcNow() + OwnerLifetime;
            var content = Secret();
            session.ContentHash = Hash(content);
            return new(session.Id, content, false);
        }
    }
    public string IssueCode(bool canvas)
    {
        lock (gate)
        {
            Prune();
            if (codes.Count >= 8) throw new InvalidOperationException("Wait for existing pairing codes to expire.");
            var code = Secret();
            codes.Add(Hash(code), (time.GetUtcNow().AddMinutes(2), canvas));
            return code;
        }
    }
    public CanvasRegistration RegisterCanvas(CatalogSnapshot catalog)
    {
        lock (gate)
        {
            var approved = canvasApproval?.Matches(catalog) == true ? canvasApproval : null;
            var registered = Register(catalog.Id, true, approved);
            return new(registered.Session.Id, registered.Secret);
        }
    }
    public PairResult Pair(PairBody body, CatalogSnapshot catalog)
    {
        lock (gate)
        {
            Prune();
            var now = time.GetUtcNow();
            while (attempts.TryPeek(out var time) && now - time >= TimeSpan.FromMinutes(1)) attempts.Dequeue();
            if (attempts.Count >= 10) throw new HttpFailure(429, "PairingThrottled", "Wait one minute and request a new local console code.");
            attempts.Enqueue(now);
            if (body.ProfileId == Guid.Empty || !ValidSecret(body.Code)
                || !codes.TryGetValue(Hash(body.Code), out var grant) || grant.Expires <= now)
                throw new HttpFailure(403, "PairingRejected", "Pairing code expired, invalid or already used.");
            ViewSession session;
            if (body.ViewId is { } id)
            {
                if (!grant.Canvas || !sessions.TryGetValue(id, out session!) || !session.Canvas || session.CatalogId != catalog.Id)
                    throw new HttpFailure(403, "PairingScope", "Use a canvas-scoped code for this view.");
                if (session.Approval is { } approval && !approval.Matches(catalog))
                    throw new HttpFailure(403, "CanvasScopeChanged", "Approved source or Bench identity changed before pairing.");
            }
            else
            {
                if (grant.Canvas || body.Attach) throw new HttpFailure(403, "PairingScope", "A browser-scoped console code is required.");
                session = Register(catalog.Id, false).Session;
            }
            codes.Remove(Hash(body.Code));
            session.ProfileId = body.ProfileId;
            session.Preferences = LoadPreferences(session);
            session.Attached = session.Canvas && body.Attach;
            var content = Secret();
            session.ContentHash = Hash(content);
            return new(session.Id, content, session.NativeContentBlocked);
        }
    }
    public ViewSession Content(Guid id, string capability, bool native)
    {
        lock (gate)
        {
            var session = Admit(id, capability, false);
            if (native && session.NativeContentBlocked)
                throw new HttpFailure(403, "CanvasDisclosureBlocked", "This canvas has no matching exact-source display approval. Use the local browser.");
            return session;
        }
    }
    public PairResult ResumeDevice(Guid requestedView, Guid deviceId, Guid profileId, CatalogSnapshot catalog)
    {
        lock (gate)
        {
            Prune();
            if (canvasApproval is null || !canvasApproval.Matches(catalog))
                throw new HttpFailure(403, "CanvasScopeChanged", "The remembered source or Bench is no longer approved.");
            if (!sessions.TryGetValue(requestedView, out var session))
                session = Register(catalog.Id, true, canvasApproval).Session;
            if (!session.Canvas || session.CatalogId != catalog.Id || session.Approval != canvasApproval)
                throw new HttpFailure(403, "DeviceScopeDenied", "This remembered device cannot authorize that view.");
            session.ProfileId = profileId;
            session.DeviceId = deviceId;
            session.Preferences = LoadPreferences(session);
            session.Attached = false;
            var content = Secret();
            session.ContentHash = Hash(content);
            return new(session.Id, content, false);
        }
    }
    public void ForgetDevice(Guid deviceId)
    {
        lock (gate)
        {
            foreach (var session in sessions.Values.Where(value => value.DeviceId == deviceId))
                Revoke(session);
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
            if (!session.Attached) throw new HttpFailure(403, "NotAttached", "Pair this canvas and explicitly attach layout controls.");
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
            if (session.Canvas && session.Approval is { } approval && !approval.Permits(value))
                throw new HttpFailure(403, "CanvasScopeDenied", "This presentation selection is outside the approved source/Bench.");
            if (session.NativeContentBlocked && (value.BenchId is not null || value.GroupSource is not null || value.GroupField is not null))
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
    private ViewPreferences LoadPreferences(ViewSession session)
    {
        var path = PreferencePath(session);
        if (!File.Exists(path))
            return session.Approval is { } approved ? new(0, approved.BenchId, "list", null, null) : new(0, null, "list", null, null);
        if (new FileInfo(path).Length > 8192) throw new InvalidDataException("Preferences exceed bound.");
        var value = JsonSerializer.Deserialize(File.ReadAllText(path), TransportJson.Default.ViewPreferences)
            ?? throw new InvalidDataException("Invalid preferences.");
        if (value.Layout is not ("tree" or "list") || value.Revision < 0) throw new InvalidDataException("Invalid preferences.");
        return !session.Canvas || session.OwnerAuthenticated ? value : session.Approval is { } approval ? approval.Restrict(value)
            : value with { BenchId = null, GroupSource = null, GroupField = null };
    }
    private string PreferencePath(ViewSession session)
        => Path.Combine(preferencesDirectory, $"{session.CatalogId:D}-{session.ProfileId:D}-{(session.Canvas ? "canvas" : "browser")}.json");
    private (ViewSession Session, string Secret) Register(Guid catalogId, bool canvas, CanvasApproval? approval = null)
    {
        Prune();
        if (sessions.Count >= 64) throw new HttpFailure(429, "ViewLimit", "Close old views or wait for expiry.");
        var token = Secret();
        var session = new ViewSession(Guid.NewGuid(), catalogId, canvas, Hash(token), time.GetUtcNow() + Lifetime, approval);
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
        if ((presentation ? value.PresentationExpires : value.Expires) <= time.GetUtcNow())
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
        foreach (var key in codes.Where(c => c.Value.Expires <= now).Select(c => c.Key).ToArray()) codes.Remove(key);
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
            lock (session) contexts = session.DisplayContexts.Values
                .Concat(new[] { session.NativeContext, session.PopulationContext }.OfType<NativePanelContext>()).ToArray();
            var references = Roots(body.Choices).Select(root =>
            {
                var context = contexts.FirstOrDefault(value => ViewSession.PathComparer.Equals(value.Root, root));
                var prior = document.Sources.FirstOrDefault(value => ViewSession.PathComparer.Equals(value.Root, root));
                return context is not null ? new NativeSourceReference(root, CatalogFingerprint.Binding(context.Source.Binding))
                    { AllowCachedDisplay = prior is not null && prior.Binding == CatalogFingerprint.Binding(context.Source.Binding)
                        && prior.AllowCachedDisplay }
                    : prior
                    ?? throw new HttpFailure(409, "ContextRequired", "Choose each intended connection explicitly before saving its reference.");
            }).ToArray();
            document = document with { Revision = checked(document.Revision + 1), Choices = body.Choices, Sources = references };
            WriteDocument(session, document);
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

    internal NativeViewStateInfo CommitRestoration(ViewSession session, long revision, Action apply)
    {
        lock (gate)
        {
            RequireOwner(session);
            using var lease = ProfileLease(session);
            var document = ReadDocument(session);
            RequireProfile(session, document);
            if (document.Revision != revision) throw StateConflict();
            apply();
            return StateInfo(session, document);
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
        return new(session.CatalogId, session.ProfileId, document.Revision, document.Choices, recovery,
            session.CurrentBenchId, session.Expires, session.Expires - OwnerRenewalLead);
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
        session.PendingCreation = null;
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
        var path = NativePath(session);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(document, TransportJson.Default.NativeViewDocument);
            if (Encoding.UTF8.GetByteCount(json) > 65536)
                throw new HttpFailure(400, "InvalidInput", "The selected view references exceed their storage bound.");
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(json);
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
    }
    private static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool ValidSecret(string? value) => value is { Length: 64 } && value.All(c => c is >= 'a' and <= 'f' or >= '0' and <= '9');
    private static bool Equal(string actual, string? expected) => expected is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expected));
}
