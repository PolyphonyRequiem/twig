using System.Text.Json;
using Twig.Domain.Common;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Serialization;
using static Twig.Infrastructure.Auth.ConnectionBindingTransitionService;

namespace Twig.Infrastructure.Auth;

internal sealed record ConnectionDefaultTransitionMemberPreview(string WorktreeRoot, string Fingerprint,
    long AttachmentRevision, string? BindingPin, bool Affected, string? State, string? OriginalBindingId,
    string? DesiredBindingId, string? Generation);
internal sealed record ConnectionDefaultTransitionPreview(string State, string Digest, bool CanApply, string ConnectionRef,
    string? OriginalBindingId, string DesiredBindingId, long? DefaultRevision,
    IReadOnlyList<ConnectionDefaultTransitionMemberPreview> Members, IReadOnlyList<string> Blockers, IReadOnlyList<string> NextSteps);

internal interface IConnectionDefaultTransitionService
{
    Task<ConnectionDefaultTransitionPreview> PreviewAsync(TwigConfiguration configuration, TwigPaths paths, string bindingId, CancellationToken ct = default);
    Task<ConnectionDefaultTransitionPreview> ApplyAsync(TwigConfiguration configuration, TwigPaths paths, string bindingId, string confirmedDigest, CancellationToken ct = default);
}

/// <summary>One reviewed default family owns admission until every affected checkout is cold and complete.</summary>
internal sealed class ConnectionDefaultTransitionService : IConnectionDefaultTransitionService, IDisposable
{
    private readonly IConnectionBindingService _bindings;
    private readonly SqliteSystemWorktreeRegistry _registry;
    private readonly ConnectionBindingTransitionService _local;
    private readonly Action<string>? _checkpoint;

    internal ConnectionDefaultTransitionService(string userHome, IConnectionBindingService bindings, Action<string>? checkpoint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userHome);
        if (!Path.IsPathFullyQualified(userHome) || !PathsEqual(Path.Combine(userHome, "system.db"), bindings.RegistryPath))
            throw new ArgumentException("Default management must use the binding service's exact native metadata home.", nameof(userHome));
        _bindings = bindings;
        _registry = new SqliteSystemWorktreeRegistry(bindings.RegistryPath, TimeProvider.System);
        _local = new ConnectionBindingTransitionService(userHome, bindings);
        _checkpoint = checkpoint;
    }

    public Task<ConnectionDefaultTransitionPreview> PreviewAsync(TwigConfiguration configuration, TwigPaths paths, string bindingId, CancellationToken ct = default)
        => ExecuteAsync(configuration, paths, bindingId, null, ct);
    public Task<ConnectionDefaultTransitionPreview> ApplyAsync(TwigConfiguration configuration, TwigPaths paths, string bindingId, string confirmedDigest, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmedDigest);
        return ExecuteAsync(configuration, paths, bindingId, confirmedDigest, ct);
    }

    private async Task<ConnectionDefaultTransitionPreview> ExecuteAsync(TwigConfiguration configuration, TwigPaths paths,
        string bindingId, string? confirmedDigest, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(configuration); ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingId);
        var connectionRef = ConnectionRefResolver.Compute(configuration);
        var blockers = new List<string>();
        var held = new List<IDisposable>();
        ConnectionDefaultTransitionRecord? record = null;
        try
        {
            var native = await _registry.ReadDefaultTransitionAsync(connectionRef, confirmedDigest, ct).ConfigureAwait(false);
            Require(native);
            record = native.Value;
            if (record is not null && record.DesiredBinding.BindingId != bindingId)
                return Blocked(connectionRef, bindingId, "binding-default-selector-conflict: resume the recorded original binding/digest, not a replacement intent.", record);
            var defaultResult = await _registry.FindDefaultBindingAsync(connectionRef, ct).ConfigureAwait(false); Require(defaultResult);
            var bindingResult = await _registry.FindBindingByIdAsync(bindingId, ct).ConfigureAwait(false); Require(bindingResult);
            if (bindingResult.Value is not { } binding || binding.ConnectionRef != connectionRef)
                return Blocked(connectionRef, bindingId, "binding-default-selector-invalid: register an explicit binding for this declared endpoint first.", record);
            if (defaultResult.Value is not { } originalDefault)
                return Blocked(connectionRef, bindingId, "binding-default-required: establish the initial default using connection bind --default; no account or legacy intent is guessed.", record);
            if (record is null && originalDefault.BindingId == bindingId)
            {
                var digest = Hash(string.Join('\n', "binding-default-unchanged-v1", connectionRef, bindingId,
                    originalDefault.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), binding.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                if (confirmedDigest is not null && digest != confirmedDigest) throw new InvalidOperationException("binding-default-preview-stale: selection/binding revision changed; rerun preview.");
                return new("unchanged", digest, true, connectionRef, bindingId, bindingId, originalDefault.Revision, [], [],
                    ["The same effective default is already selected. Read data, runtime admission, pending work, credentials and histories are unchanged."]);
            }
            var registrations = await _registry.ReadDefaultWorktreesAsync(connectionRef, ct).ConfigureAwait(false); Require(registrations);
            var rows = record?.Members.Select(x => x.Registration).ToArray() ?? registrations.Value.ToArray();
            if (!rows.SequenceEqual(registrations.Value))
                return Blocked(connectionRef, bindingId, "binding-default-worktree-set-cas-mismatch: registered membership/revisions differ from the immutable family; restore/recover the original native authority.", record);
            // Like local pin management, take operation gates before attachment CAS locks.
            // Every family uses the registry's stable fingerprint order. Pinned operations stay untouched.
            var gated = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                ValidateRoot(row);
                if (ReadAttachment(MemberPaths(row, paths)).BindingPin is null)
                {
                    held.Add(ConnectionOperationGate.Acquire(row.WorktreeRoot, exclusive: true));
                    gated.Add(row.Fingerprint);
                }
            }
            foreach (var row in rows)
            {
                held.Add(new FileStream(Path.Combine(row.WorktreeRoot, ".twig", WorktreeLocalAttachmentStore.AttachmentLockFileName),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            foreach (var row in rows)
                if (ReadAttachment(MemberPaths(row, paths)).BindingPin is null && !gated.Contains(row.Fingerprint))
                    return Blocked(connectionRef, bindingId, "binding-default-member-pin-cas-mismatch: an attachment became unpinned while gates were acquired; review fresh membership.", record);
            if (record is null)
            {
                var identity = (await _bindings.ListIdentitiesAsync(ct).ConfigureAwait(false)).SingleOrDefault(x => x.IdentityId == binding.IdentityId)
                    ?? throw new InvalidOperationException("binding-default-identity-missing: repair the desired identity separately.");
                var desiredDefault = new ConnectionDefaultSelection(connectionRef, bindingId, checked(originalDefault.Revision + 1));
                var members = new List<ConnectionDefaultMemberRecord>();
                foreach (var row in rows)
                {
                    ct.ThrowIfCancellationRequested();
                    var memberPaths = MemberPaths(row, paths);
                    var memberConfiguration = await TwigConfiguration.LoadSplitAsync(memberPaths, ct).ConfigureAwait(false);
                    using var attachmentStore = new WorktreeLocalAttachmentStore(memberPaths, memberConfiguration, TimeProvider.System);
                    var attachmentResult = await attachmentStore.ReadWithRevisionAsync(ct).ConfigureAwait(false); Require(attachmentResult);
                    var document = ReadAttachment(memberPaths);
                    if (ConnectionRefResolver.Compute(memberConfiguration) != connectionRef || document.ConnectionRef != connectionRef
                        || document.Revision != attachmentResult.Value.Revision)
                        throw new InvalidOperationException("binding-default-attachment-context-invalid: endpoint/fingerprint/revision does not match registered membership.");
                    ConnectionBindingTransitionRecord? local = null;
                    if (document.BindingPin is null)
                    {
                        var prepared = await _local.PrepareDefaultMemberAsync(memberConfiguration, memberPaths, bindingId, desiredDefault.Revision, ct).ConfigureAwait(false);
                        blockers.AddRange(prepared.Preview.Blockers.Select(x => row.WorktreeRoot + ": " + x));
                        local = prepared.Record;
                        if (local is null && prepared.Preview.CanApply)
                            blockers.Add(row.WorktreeRoot + ": binding-default-member-not-prepared: no native recoverable member context was admitted.");
                    }
                    members.Add(new(row, document, ConfigurationHash(memberPaths), HashFile(Path.Combine(memberPaths.TwigDir, WorktreeLocalAttachmentStore.WorktreeFileName)),
                        HashFile(Path.Combine(memberPaths.TwigDir, WorktreeLocalAttachmentStore.LayoutFileName)), HashFile(AttachmentPath(memberPaths)), local));
                }
                var desiredBinding = new IdentityBinding(binding.BindingId, binding.ConnectionRef, binding.IdentityId, binding.Revision);
                record = new(1, "", connectionRef, "preparing", new(connectionRef, originalDefault.BindingId, originalDefault.Revision), desiredDefault, desiredBinding, identity, members);
                record = record with { Digest = Digest(record) };
            }
            await ValidateFamilyAsync(record, paths, blockers, ct).ConfigureAwait(false);
            var authority = await _registry.InspectDefaultTransitionAuthorityAsync(record, ct).ConfigureAwait(false);
            if (!authority.IsSuccess) blockers.Add(authority.Error!);
            if (record.State != "completed" && blockers.Count == 0)
            {
                foreach (var member in record.Members)
                {
                    if (member.Transition is not { } local) continue;
                    var memberPaths = MemberPaths(member.Registration, paths);
                    var memberConfiguration = await TwigConfiguration.LoadSplitAsync(memberPaths, ct).ConfigureAwait(false);
                    await _local.InspectUnfinishedWorkAsync(local, memberConfiguration, memberPaths, ct, blockers).ConfigureAwait(false);
                }
                try { await _bindings.VerifyIdentityCredentialAsync(record.DesiredIdentity.Name, configuration.Organization, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { blockers.Add("binding-default-credential-not-admitted: " + ex.Message); }
            }
            if (blockers.Count > 0) return Report(record, blockers);
            if (confirmedDigest is null) return Report(record, []);
            if (record.Digest != confirmedDigest)
                throw new InvalidOperationException("binding-default-preview-stale: exact digest no longer matches every registered worktree, attachment, binding and default revision. No transition occurred; review a fresh preview.");
            if (record.State == "completed") return Report(record, []);
            if (native.Value is null)
            {
                Require(await _registry.BeginDefaultTransitionAsync(record, ct).ConfigureAwait(false));
                _checkpoint?.Invoke("intent-recorded");
            }
            if (record.State == "preparing")
            {
                Require(await _registry.CommitDefaultTransitionAsync(record, ct).ConfigureAwait(false));
                record = record with { State = "central-committed", Members = record.Members.Select(x => x.Transition is { } local
                    ? x with { Transition = local with { State = "central-committed" } } : x).ToArray() };
                _checkpoint?.Invoke("central-committed");
            }
            for (var i = 0; i < record.Members.Count; i++)
            {
                if (record.Members[i].Transition is not { } local) continue;
                var memberPaths = MemberPaths(record.Members[i].Registration, paths);
                if (local.State == "central-committed")
                {
                    // Default changes do not alter the local pin, primary scope, holder, or attachment revision.
                    if (ReadAttachment(memberPaths) != local.OriginalAttachment || HashFile(AttachmentPath(memberPaths)) != local.AttachmentHash)
                        throw new InvalidOperationException("binding-default-attachment-cas-mismatch: restore the exact original attachment before recovery.");
                    record = await AdvanceMemberAsync(record, i, "attachment-completed", ct).ConfigureAwait(false);
                    local = record.Members[i].Transition!;
                    _checkpoint?.Invoke("attachment-completed:" + i);
                }
                if (local.State == "attachment-completed")
                {
                    if (local.ResetRequired) ResetMirror(local);
                    _checkpoint?.Invoke("mirror-reset:" + i);
                    var marker = new MirrorAdmissionDocument(1, _bindings.RegistryPath, local.Fingerprint, local.ConnectionRef,
                        local.DesiredBinding.Binding.BindingId, local.Generation, Path.GetFileName(local.CurrentMirror));
                    await WriteAtomicAsync(Path.Combine(memberPaths.TwigDir, "cache", MirrorAdmission.MarkerFile),
                        JsonSerializer.Serialize(marker, TwigJsonContext.Default.MirrorAdmissionDocument), ct).ConfigureAwait(false);
                    record = await AdvanceMemberAsync(record, i, "mirror-completed", ct).ConfigureAwait(false);
                    local = record.Members[i].Transition!;
                    _checkpoint?.Invoke("mirror-completed:" + i);
                }
                if (local.State == "mirror-completed")
                {
                    VerifyCompletedMember(local, memberPaths, requireCold: true);
                    record = await AdvanceMemberAsync(record, i, "completed", ct).ConfigureAwait(false);
                    _checkpoint?.Invoke("member-completed:" + i);
                }
            }
            blockers.Clear(); await ValidateFamilyAsync(record, paths, blockers, ct).ConfigureAwait(false);
            if (blockers.Count > 0) throw new InvalidOperationException(string.Join('\n', blockers));
            foreach (var member in record.Members)
                if (member.Transition is { } local) VerifyCompletedMember(local, MemberPaths(member.Registration, paths), requireCold: true);
            var completed = record with { State = "completed" };
            Require(await _registry.AdvanceDefaultTransitionAsync(record, completed, ct).ConfigureAwait(false));
            record = completed;
            _checkpoint?.Invoke("completed");
            foreach (var member in record.Members)
                if (member.Transition is not null) _ = MirrorAdmission.Acquire(MemberPaths(member.Registration, paths), _bindings.RegistryPath)
                    ?? throw new InvalidOperationException("binding-default-readback-failed: exact completed family has no admitted mirror.");
            return Report(record, []);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or JsonException)
        {
            return Blocked(connectionRef, bindingId, ex.Message, record);
        }
        finally { for (var i = held.Count - 1; i >= 0; i--) held[i].Dispose(); }
    }

    private async Task<ConnectionDefaultTransitionRecord> AdvanceMemberAsync(ConnectionDefaultTransitionRecord original, int index, string state, CancellationToken ct)
    {
        var members = original.Members.ToArray();
        members[index] = members[index] with { Transition = members[index].Transition! with { State = state } };
        var desired = original with { Members = members };
        Require(await _registry.AdvanceDefaultTransitionAsync(original, desired, ct).ConfigureAwait(false));
        return desired;
    }

    private async Task ValidateFamilyAsync(ConnectionDefaultTransitionRecord record, TwigPaths template, List<string> blockers, CancellationToken ct)
    {
        if (record.Version != 1 || record.Digest != Digest(record)
            || record.OriginalDefault.ConnectionRef != record.ConnectionRef || record.DesiredDefault.ConnectionRef != record.ConnectionRef
            || record.DesiredDefault.BindingId != record.DesiredBinding.BindingId
            || record.DesiredDefault.Revision != checked(record.OriginalDefault.Revision + 1)
            || record.DesiredBinding.IdentityId != record.DesiredIdentity.IdentityId)
            blockers.Add("binding-default-intent-invalid: native immutable family context/digest is inconsistent.");
        foreach (var member in record.Members)
        {
            ValidateRoot(member.Registration);
            var paths = MemberPaths(member.Registration, template);
            var currentAttachment = ReadAttachment(paths);
            var affectedContextChanged = member.Transition is not null
                && (member.Attachment != currentAttachment || member.AttachmentHash != HashFile(AttachmentPath(paths))
                    || member.ConfigurationHash != ConfigurationHash(paths));
            var pinQualificationChanged = member.Transition is null
                && (currentAttachment.BindingPin is null || currentAttachment.ConnectionRef != member.Attachment.ConnectionRef);
            if (affectedContextChanged || pinQualificationChanged
                || member.WorktreeHash != HashFile(Path.Combine(paths.TwigDir, WorktreeLocalAttachmentStore.WorktreeFileName))
                || member.LayoutHash != HashFile(Path.Combine(paths.TwigDir, WorktreeLocalAttachmentStore.LayoutFileName)))
                blockers.Add(member.Registration.WorktreeRoot + ": binding-default-member-context-changed: affected attachment or membership qualification changed; restore the original intent context.");
            if (member.Transition is { } local)
            {
                if (member.Attachment.BindingPin is not null || local.Fingerprint != member.Registration.Fingerprint
                    || local.OriginalAttachment != member.Attachment || local.DesiredAttachment != member.Attachment
                    || local.DesiredBinding.Binding != record.DesiredBinding || local.DesiredBinding.SelectionSource != "connection-default-binding"
                    || local.DesiredBinding.SelectionRevision != record.DesiredDefault.Revision)
                    blockers.Add("binding-default-member-invalid: affected membership and frozen desired authority disagree.");
                var configuration = await TwigConfiguration.LoadSplitAsync(paths, ct).ConfigureAwait(false);
                ValidateRecord(local, paths, configuration, local.Fingerprint, blockers);
                if (record.State == "completed") VerifyCompletedMember(local, paths, requireCold: false);
            }
            else if (member.Attachment.BindingPin is null)
                blockers.Add("binding-default-unprepared-member: every registered unpinned checkout must be safely prepared.");
        }
    }

    private void VerifyCompletedMember(ConnectionBindingTransitionRecord local, TwigPaths paths, bool requireCold)
    {
        if (ReadAttachment(paths) != local.DesiredAttachment) throw new InvalidOperationException("binding-default-attachment-completion-invalid");
        var marker = MirrorAdmission.ReadDocument(Path.Combine(paths.TwigDir, "cache", MirrorAdmission.MarkerFile));
        if (!PathsEqual(marker.RegistryPath, _bindings.RegistryPath) || marker.Fingerprint != local.Fingerprint
            || marker.BindingId != local.DesiredBinding.Binding.BindingId || marker.ConnectionRef != local.ConnectionRef
            || marker.Generation != local.Generation || marker.MirrorFile != Path.GetFileName(local.CurrentMirror))
            throw new InvalidOperationException("binding-default-marker-cas-mismatch: restore the exact recorded generation; no partial admission is allowed.");
        VerifyMirrorGeneration(requireCold ? local : local with { ResetRequired = false });
    }

    private static TwigPaths MemberPaths(ConnectionDefaultWorktreeRow row, TwigPaths template)
    {
        var twigDir = Path.Combine(row.WorktreeRoot, ".twig");
        return new TwigPaths(twigDir, Path.Combine(twigDir, "config"), MirrorAdmission.ResolveMirrorPath(twigDir), row.WorktreeRoot, template.GlobalDisplayPath);
    }
    private static void ValidateRoot(ConnectionDefaultWorktreeRow row)
    {
        if (!Path.IsPathFullyQualified(row.WorktreeRoot) || !Directory.Exists(row.WorktreeRoot)
            || !WorktreeAnchorDetector.TryDetect(row.WorktreeRoot, out var anchor, out _)
            || !PathsEqual(anchor.WorktreeRoot, row.WorktreeRoot) || WorktreeFingerprintProvider.CanonicalJson(anchor) != row.Fingerprint)
            throw new InvalidOperationException("binding-default-member-unreachable: registered worktree cannot be inspected at its exact fingerprint; restore it before changing the default.");
    }
    private static string Digest(ConnectionDefaultTransitionRecord record) => Hash(JsonSerializer.Serialize(record with
    {
        Digest = "", State = "preparing", Members = record.Members.Select(x => x.Transition is { } local
            ? x with { Transition = local with { State = "preparing" } } : x).ToArray()
    }, TwigJsonContext.Default.ConnectionDefaultTransitionRecord));
    private static ConnectionDefaultTransitionPreview Report(ConnectionDefaultTransitionRecord record, IReadOnlyList<string> blockers)
        => new(record.State == "preparing" ? "preview" : record.State, record.Digest, blockers.Count == 0,
            record.ConnectionRef, record.OriginalDefault.BindingId, record.DesiredDefault.BindingId, record.OriginalDefault.Revision,
            record.Members.Select(x => new ConnectionDefaultTransitionMemberPreview(x.Registration.WorktreeRoot,
                x.Registration.Fingerprint, x.Attachment.Revision, x.Attachment.BindingPin, x.Transition is not null,
                x.Transition?.State, x.Transition?.OriginalBinding.Binding.BindingId, x.Transition?.DesiredBinding.Binding.BindingId, x.Transition?.Generation)).ToArray(),
            blockers, blockers.Count > 0
                ? ["Resolve each blocker separately under its original authority; recover the exact recorded default family/digest. No force, publication, discard, claim release or automatic migration is permitted."]
                : [record.State == "completed" ? "All affected worktrees are cold and admitted together. Explicitly reconnect affected live hosts; pinned checkouts remain unchanged."
                    : "Apply/resume this exact default digest. Every affected checkout remains fenced until the entire native family completes; credentials, portable policy, scopes and durable histories are preserved."]);
    private static ConnectionDefaultTransitionPreview Blocked(string connectionRef, string bindingId, string reason, ConnectionDefaultTransitionRecord? record = null)
        => record is not null ? Report(record, [reason]) : new("blocked", "", false, connectionRef, null, bindingId, null, [], [reason],
            ["Restore/settle the blocker separately, then rerun preview. No force or partial admission is available."]);
    private static void Require(Result result) { if (!result.IsSuccess) throw new InvalidOperationException(result.Error); }
    private static void Require<T>(Result<T> result) { if (!result.IsSuccess) throw new InvalidOperationException(result.Error); }
    public void Dispose() { _local.Dispose(); _registry.Dispose(); }
}
