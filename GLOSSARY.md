# Twig work management

Twig's work-management language covers provider work items, unpublished intent,
and how people organize and change that work.

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
_Avoid_: Provider, resource scope.

**Local ID**:
The durable identity of a seed, independent of its seed alias.

**Seed alias**:
A shorthand for addressing a seed, distinct from its local ID.
_Avoid_: Native ID.

**Revision**:
An opaque, item-specific marker of the version observed. It is not part of the work item key and does not imply protection against stale writes.

## Work Item Structure

**Item type**:
A kind of work item that determines which field definitions and workflow states apply.

**Field definition**:
The description of a field that applies to an item type, including what kind of value it can hold. A shared display label does not make two field definitions the same.

**Field value**:
The content of a field on a particular item, as distinct from the field's definition.

**Unset field**:
A field observed to have no value.

**Unobserved field**:
A field for which no observation is available. Its value, or lack of one, is unknown.

**Last-known field**:
A field whose most recent observation is available but not known to be current. That observation may record a value or show that the field was unset.

**State**:
An exact position in an item type's workflow. An item's state is a value of its state field.

**State category**:
A broader classification that can group multiple states without making them equivalent.

**Relationship**:
A named connection between identified items. A provider-owned relationship is recorded by a provider; a Twig-owned relationship is recorded by Twig and need not be known to either item's provider.

**Resource link**:
A reference to a resource by its locator. A locator that opens a work item does not by itself establish a relationship to that item.

**Work item extension**:
Twig-owned data attached to an existing work item by its work item key. It is neither a second work item nor data owned by the original item's provider.

## Local Edits

**Local edits**:
Changes Twig retains as intent for an existing work item, not a copy of the item or a seed. An omitted field is left untouched; explicitly clearing a field is an edit, not an observation that the field is unset.
_Avoid_: Work item draft.

**Edit baseline**:
The provider observation retained as the basis for local edits. It can differ from the latest provider observation.
_Avoid_: Draft baseline.

## Plans

**Plan**:
A defined set of intended work-item changes, distinct from editable local intent and from the outcome of applying those changes.
_Avoid_: Proposal.

**Plan unit**:
One ordered operation in a Plan, with its own outcome and evidence. Observing the outcome of a unit does not resubmit it.

**Action**:
A deliberately reviewed change to publish for one work item, distinct from its editable private input.

**Submission**:
The durable execution identity for an Action. Observing it recovers the outcome of the original attempt rather than sending the Action again.

## Benches

**Bench**:
A named, user-owned set of rules for gathering work across one or more connections.
_Avoid_: Workspace, when referring to the Bench itself.

**Bench rule**:
A criterion for including work in a Bench, such as a pin or a query.

**Bench membership**:
The work resolved from a Bench's rules against available observations, not the rules themselves.

**Workspace exclusion**:
A workspace-specific rule for omitting work from a Bench view without changing the Bench's rules.
