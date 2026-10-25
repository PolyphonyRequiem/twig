using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Persistence.ProviderTargets;
using Twig.Infrastructure.Serialization;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence.ProviderTargets;

public sealed class ProviderTargetBindingPersistenceTests : IDisposable
{
    private readonly string _worktreeRoot;
    private readonly string _twigDir;
    private readonly string _systemDbPath;
    private readonly TwigPaths _paths;
    private readonly SqliteSystemWorktreeRegistry _registry;
    private readonly WorktreeLocalProviderTargetAttachmentStore _attachmentStore;
    private readonly ProviderTargetAttachmentService _service;

    public ProviderTargetBindingPersistenceTests()
    {
        _worktreeRoot = Path.Combine(Path.GetTempPath(), "twig-provider-target-" + Guid.NewGuid().ToString("N"));
        _twigDir = Path.Combine(_worktreeRoot, ".twig");
        _systemDbPath = Path.Combine(_worktreeRoot, "system.db");
        Directory.CreateDirectory(_worktreeRoot);
        _paths = new TwigPaths(
            _twigDir,
            Path.Combine(_twigDir, "config"),
            Path.Combine(_twigDir, "cache", "twig.db"),
            _worktreeRoot);
        _registry = new SqliteSystemWorktreeRegistry(_systemDbPath, TimeProvider.System);
        _attachmentStore = new WorktreeLocalProviderTargetAttachmentStore(
            _paths, _registry, TimeProvider.System);
        _service = new ProviderTargetAttachmentService(_registry, _attachmentStore);
        if (!TryInitGit(_worktreeRoot))
            throw new InvalidOperationException("Git is required to exercise provider-target worktree attachment tests.");
    }

    public void Dispose()
    {
        _attachmentStore.Dispose();
        _registry.Dispose();
        try { Directory.Delete(_worktreeRoot, recursive: true); } catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task GetOrCreate_is_concurrent_idempotent_and_does_not_refresh_existing_metadata()
    {
        var identity = Identity();
        var creates = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(index => _registry.GetOrCreateAsync(identity, $"locator-{index}", $"display-{index}")));

        creates.ShouldAllBe(result => result.IsSuccess);
        creates.Select(result => result.Value.Id).Distinct().Count().ShouldBe(1);
        creates.Select(result => result.Value.Revision).Distinct().Count().ShouldBe(1);

        var original = creates[0].Value;
        var repeated = await _registry.GetOrCreateAsync(identity, "new-locator", "new-display");

        repeated.IsSuccess.ShouldBeTrue(repeated.Error);
        repeated.Value.ShouldBe(original);
        repeated.Value.Locator.ShouldBe(original.Locator);
        repeated.Value.DisplayName.ShouldBe(original.DisplayName);
    }

    [Fact]
    public async Task Separate_registry_instances_share_identity_uniqueness_and_metadata_cas()
    {
        using var secondRegistry = new SqliteSystemWorktreeRegistry(_systemDbPath, TimeProvider.System);
        var identity = Identity();
        var creates = await Task.WhenAll(
            _registry.GetOrCreateAsync(identity, "first", "First"),
            secondRegistry.GetOrCreateAsync(identity, "second", "Second"));

        creates.ShouldAllBe(result => result.IsSuccess);
        creates[0].Value.Id.ShouldBe(creates[1].Value.Id);

        var updates = await Task.WhenAll(
            _registry.UpdateMetadataAfterIdentityConfirmationAsync(
                creates[0].Value.Id, identity, creates[0].Value.Revision, "winner-a", "Winner A"),
            secondRegistry.UpdateMetadataAfterIdentityConfirmationAsync(
                creates[1].Value.Id, identity, creates[1].Value.Revision, "winner-b", "Winner B"));

        updates.Count(result => result.IsSuccess).ShouldBe(1);
        updates.Count(result => !result.IsSuccess
            && result.Error == ProviderTargetBindingFailures.RevisionMismatch).ShouldBe(1);
        var persisted = await _registry.FindByIdAsync(creates[0].Value.Id);
        persisted.IsSuccess.ShouldBeTrue(persisted.Error);
        persisted.Value!.Revision.ShouldBe(creates[0].Value.Revision + 1);
        new[] { ("winner-a", "Winner A"), ("winner-b", "Winner B") }
            .ShouldContain((persisted.Value.Locator!, persisted.Value.DisplayName!));
    }

    [Fact]
    public async Task Identity_uniqueness_is_ordinal_and_separated_by_provider_scope_and_descriptor()
    {
        var identities = new[]
        {
            Identity(),
            Identity(providerKind: "ado"),
            Identity(resourceScope: "https://api.github.com/other"),
            Identity(descriptorKind: "github.organization/v1"),
            Identity(descriptorValue: "repo:Repo-42"),
            Identity(descriptorValue: "repo:repo-42"),
        };

        var bindings = await Task.WhenAll(identities.Select(identity =>
            _registry.GetOrCreateAsync(identity, null, null)));

        bindings.ShouldAllBe(result => result.IsSuccess);
        bindings.Select(result => result.Value.Id).Distinct().Count().ShouldBe(identities.Length);
    }

    [Fact]
    public async Task Metadata_update_requires_matching_confirmed_identity_and_revision()
    {
        var identity = Identity();
        var created = await _registry.GetOrCreateAsync(identity, "old-locator", "Old name");
        created.IsSuccess.ShouldBeTrue(created.Error);

        var differentIdentity = Identity(descriptorValue: "repo:other");
        var identityMismatch = await _registry.UpdateMetadataAfterIdentityConfirmationAsync(
            created.Value.Id, differentIdentity, created.Value.Revision, "wrong", "Wrong");
        identityMismatch.IsSuccess.ShouldBeFalse();
        identityMismatch.Error.ShouldBe(ProviderTargetBindingFailures.IdentityMismatch);

        var revisionMismatch = await _registry.UpdateMetadataAfterIdentityConfirmationAsync(
            created.Value.Id, identity, created.Value.Revision + 1, "stale", "Stale");
        revisionMismatch.IsSuccess.ShouldBeFalse();
        revisionMismatch.Error.ShouldBe(ProviderTargetBindingFailures.RevisionMismatch);

        var updated = await _registry.UpdateMetadataAfterIdentityConfirmationAsync(
            created.Value.Id, identity, created.Value.Revision, "new-locator", "New name");
        updated.IsSuccess.ShouldBeTrue(updated.Error);
        updated.Value.Identity.ShouldBe(identity);
        updated.Value.Locator.ShouldBe("new-locator");
        updated.Value.DisplayName.ShouldBe("New name");
        updated.Value.Revision.ShouldBe(created.Value.Revision + 1);
    }

    [Fact]
    public async Task Attachment_writes_a_neutral_source_generated_document_for_the_live_worktree()
    {
        var result = await _service.AttachAsync(Identity(), "owner/repository", "Repository", expectedRevision: 0);

        result.IsSuccess.ShouldBeTrue(result.Error);
        result.Value.AttachmentRevision.ShouldBe(1);
        var path = Path.Combine(_twigDir, ProviderTargetAttachmentDocument.FileName);
        File.Exists(path).ShouldBeTrue();

        var document = JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(path),
            TwigJsonContext.Default.ProviderTargetAttachmentDocument);
        document.ShouldNotBeNull();
        document!.Schema.ShouldBe(ProviderTargetAttachmentDocument.CurrentSchema);
        document.WorktreeFingerprint.ShouldNotBeNullOrWhiteSpace();
        document.BindingId.ShouldBe(result.Value.Binding.Id.ToString());

        var read = await _attachmentStore.ReadWithRevisionAsync();
        read.IsSuccess.ShouldBeTrue(read.Error);
        read.Value.Envelope.ShouldNotBeNull();
        read.Value.Envelope!.BindingId.ShouldBe(result.Value.Binding.Id);
        read.Value.Revision.ShouldBe(1);
    }

    [Fact]
    public async Task Stale_attachment_cas_keeps_old_pointer_and_retry_reuses_the_durable_binding()
    {
        var first = await _service.AttachAsync(Identity(descriptorValue: "repo:first"), null, null, 0);
        first.IsSuccess.ShouldBeTrue(first.Error);

        var secondIdentity = Identity(descriptorValue: "repo:second");
        var refused = await _service.AttachAsync(secondIdentity, "second", "Second", expectedRevision: 0);
        refused.IsSuccess.ShouldBeFalse();
        refused.Error.ShouldBe(ProviderTargetAttachmentFailures.RevisionMismatch);

        var secondBinding = await _registry.FindByIdentityAsync(secondIdentity);
        secondBinding.IsSuccess.ShouldBeTrue(secondBinding.Error);
        secondBinding.Value.ShouldNotBeNull();
        var afterRefusal = await _attachmentStore.ReadWithRevisionAsync();
        afterRefusal.IsSuccess.ShouldBeTrue(afterRefusal.Error);
        afterRefusal.Value.Envelope!.BindingId.ShouldBe(first.Value.Binding.Id);
        afterRefusal.Value.Revision.ShouldBe(1);

        var retried = await _service.AttachAsync(secondIdentity, "ignored", "Ignored", expectedRevision: 1);
        retried.IsSuccess.ShouldBeTrue(retried.Error);
        retried.Value.Binding.Id.ShouldBe(secondBinding.Value!.Id);
        retried.Value.Binding.Locator.ShouldBe("second");
        retried.Value.Binding.DisplayName.ShouldBe("Second");
        retried.Value.AttachmentRevision.ShouldBe(2);
        (await _registry.FindByIdentityAsync(first.Value.Binding.Identity)).Value.ShouldNotBeNull();
    }

    [Fact]
    public async Task Attachment_fails_closed_when_its_binding_row_is_missing()
    {
        var attached = await _service.AttachAsync(Identity(), null, null, 0);
        attached.IsSuccess.ShouldBeTrue(attached.Error);

        await using (var connection = new SqliteConnection($"Data Source={_systemDbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM provider_target_bindings WHERE binding_id = $id;";
            command.Parameters.AddWithValue("$id", attached.Value.Binding.Id.ToString());
            (await command.ExecuteNonQueryAsync()).ShouldBe(1);
        }

        var read = await _attachmentStore.ReadWithRevisionAsync();
        read.IsSuccess.ShouldBeFalse();
        read.Error.ShouldBe(ProviderTargetAttachmentFailures.BindingMissing);
    }

    [Fact]
    public async Task Attachment_directory_at_document_path_fails_closed_without_replacement()
    {
        var binding = await _registry.GetOrCreateAsync(Identity(), null, null);
        binding.IsSuccess.ShouldBeTrue(binding.Error);
        var path = Path.Combine(_twigDir, ProviderTargetAttachmentDocument.FileName);
        Directory.CreateDirectory(path);

        var read = await _attachmentStore.ReadWithRevisionAsync();
        read.IsSuccess.ShouldBeFalse();
        read.Error.ShouldBe(ProviderTargetAttachmentFailures.ReadFailed);

        var write = await _attachmentStore.WriteAsync(binding.Value.Id, expectedRevision: 0);
        write.IsSuccess.ShouldBeFalse();
        write.Error.ShouldBe(ProviderTargetAttachmentFailures.ReadFailed);
        Directory.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public async Task Invalid_attachment_document_cannot_be_overwritten_as_a_missing_pointer()
    {
        var binding = await _registry.GetOrCreateAsync(Identity(), null, null);
        binding.IsSuccess.ShouldBeTrue(binding.Error);
        Directory.CreateDirectory(_twigDir);
        var path = Path.Combine(_twigDir, ProviderTargetAttachmentDocument.FileName);
        const string invalidDocument = "{\"schema\":\"foreign-document\"}";
        await File.WriteAllTextAsync(path, invalidDocument);

        var write = await _attachmentStore.WriteAsync(binding.Value.Id, expectedRevision: 0);
        write.IsSuccess.ShouldBeFalse();
        write.Error.ShouldBe(ProviderTargetAttachmentFailures.InvalidDocument);
        (await File.ReadAllTextAsync(path)).ShouldBe(invalidDocument);
    }

    [Fact]
    public async Task Attachment_refuses_a_different_live_worktree_fingerprint()
    {
        var attached = await _service.AttachAsync(Identity(), null, null, 0);
        attached.IsSuccess.ShouldBeTrue(attached.Error);
        var path = Path.Combine(_twigDir, ProviderTargetAttachmentDocument.FileName);
        var document = JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(path),
            TwigJsonContext.Default.ProviderTargetAttachmentDocument)!;
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(
            document with { WorktreeFingerprint = "different-worktree" },
            TwigJsonContext.Default.ProviderTargetAttachmentDocument));

        var read = await _attachmentStore.ReadWithRevisionAsync();
        read.IsSuccess.ShouldBeFalse();
        read.Error.ShouldBe(ProviderTargetAttachmentFailures.WorktreeFingerprintMismatch);
    }

    [Fact]
    public async Task Clear_advances_revision_and_keeps_a_tombstone_for_safe_reattachment()
    {
        var attached = await _service.AttachAsync(Identity(), null, null, 0);
        attached.IsSuccess.ShouldBeTrue(attached.Error);

        var cleared = await _attachmentStore.ClearAsync(attached.Value.Binding.Id, expectedRevision: 1);
        cleared.IsSuccess.ShouldBeTrue(cleared.Error);
        cleared.Value.WrittenRevision.ShouldBe(2);

        var read = await _attachmentStore.ReadWithRevisionAsync();
        read.IsSuccess.ShouldBeTrue(read.Error);
        read.Value.Envelope!.State.ShouldBe(ProviderTargetAttachmentState.Detached);
        read.Value.Envelope.BindingId.ShouldBeNull();
        read.Value.Revision.ShouldBe(2);

        var staleWrite = await _attachmentStore.WriteAsync(attached.Value.Binding.Id, expectedRevision: 0);
        staleWrite.IsSuccess.ShouldBeFalse();
        staleWrite.Error.ShouldBe(ProviderTargetAttachmentFailures.RevisionMismatch);
        (await _attachmentStore.ReadWithRevisionAsync()).Value.Revision.ShouldBe(2);

        var reattached = await _attachmentStore.WriteAsync(attached.Value.Binding.Id, expectedRevision: 2);
        reattached.IsSuccess.ShouldBeTrue(reattached.Error);
        reattached.Value.WrittenRevision.ShouldBe(3);
    }

    [Fact]
    public async Task Separate_attachment_store_instances_allow_only_one_cas_writer()
    {
        using var secondRegistry = new SqliteSystemWorktreeRegistry(_systemDbPath, TimeProvider.System);
        using var secondStore = new WorktreeLocalProviderTargetAttachmentStore(
            _paths, secondRegistry, TimeProvider.System);

        var firstBinding = await _registry.GetOrCreateAsync(Identity(descriptorValue: "repo:first"), null, null);
        var secondBinding = await secondRegistry.GetOrCreateAsync(Identity(descriptorValue: "repo:second"), null, null);
        firstBinding.IsSuccess.ShouldBeTrue(firstBinding.Error);
        secondBinding.IsSuccess.ShouldBeTrue(secondBinding.Error);
        (await _attachmentStore.WriteAsync(firstBinding.Value.Id, expectedRevision: 0)).IsSuccess.ShouldBeTrue();

        var writes = await Task.WhenAll(
            _attachmentStore.WriteAsync(secondBinding.Value.Id, expectedRevision: 1),
            secondStore.WriteAsync(firstBinding.Value.Id, expectedRevision: 1));

        writes.Count(result => result.IsSuccess).ShouldBe(1);
        writes.Count(result => !result.IsSuccess
            && result.Error == ProviderTargetAttachmentFailures.RevisionMismatch).ShouldBe(1);
        var final = await _attachmentStore.ReadWithRevisionAsync();
        final.IsSuccess.ShouldBeTrue(final.Error);
        final.Value.Revision.ShouldBe(2);
        new[] { firstBinding.Value.Id, secondBinding.Value.Id }
            .ShouldContain(final.Value.Envelope!.BindingId!.Value);
    }

    private static ProviderTargetIdentity Identity(
        string providerKind = "github",
        string resourceScope = "https://api.github.com",
        string descriptorKind = "github.repository/v1",
        string descriptorValue = "repo:owner-42/repo-73") =>
        new(providerKind, resourceScope, descriptorKind, descriptorValue);

    private static bool TryInitGit(string directory)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", "init -q")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null || !process.WaitForExit(5_000))
                return false;
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
