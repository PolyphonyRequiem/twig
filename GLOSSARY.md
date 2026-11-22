# Twig glossary

## Work Item Identity

**Work item**:
An item with an identity issued by a provider.

**Seed**:
A new, unpublished item with a local identity and no provider-issued identity yet.
_Avoid_: Local work item, unpublished work item.

**Provider**:
The work-tracking authority for its work items. Azure DevOps, GitHub, and the Twig provider are all providers.
_Avoid_: Connection.

**Resource scope**:
The namespace a provider defines for its native IDs.

**Native ID**:
The identifier a provider assigns to a work item within its resource scope.
_Avoid_: Work item key when referring to the native ID alone.

**Work item key**:
The full identity of a work item: its provider, resource scope, and native ID.

**Connection**:
The means of accessing a provider at a particular destination under a particular identity.

**Local ID**:
The durable identity of a seed, independent of its seed alias.

**Seed alias**:
A shorthand for addressing a seed in the CLI, distinct from its local ID.
_Avoid_: Native ID.

**Revision**:
An opaque, item-specific marker of the version observed. It is not part of the work item key and does not imply protection against stale writes.
