import { createHash, randomUUID } from "node:crypto";
import { mkdir, realpath as nodeRealpath, writeFile } from "node:fs/promises";
import path from "node:path";
import { createNativeCommandRunner } from "./native-editing.mjs";
import { createNativeProposalReader } from "./native-proposal.mjs";
import { validateApplyResult } from "./native-apply.mjs";
import { sameDiscussionContext } from "./discussion-document.mjs";

const record = value => value !== null && typeof value === "object" && !Array.isArray(value);
function requireValue(condition) {
    if (!condition) throw new Error("Native discussion proposal is invalid or does not match the captured request.");
}
function inside(root, file) {
    const relative = path.relative(root, file);
    return !!relative && relative !== ".." && !relative.startsWith(".." + path.sep) && !path.isAbsolute(relative);
}
export function validateDiscussionPreparation(value, { context, text, format, exitCode }) {
    requireValue(value?.version === 1 && ["prepared", "refused"].includes(value.status));
    if (value.status === "refused") {
        requireValue([1, 2].includes(exitCode) && value.proposal === null && value.digest === null && value.context === null);
        throw new Error("Native discussion preparation refused; read the qualified discussion again. No comment was posted.");
    }
    requireValue(exitCode === 0 && value.error === null && sameDiscussionContext(value.context, context) &&
        typeof value.proposal === "string" && /^[a-f0-9]{64}$/.test(value.digest) &&
        createHash("sha256").update(value.proposal, "utf8").digest("hex") === value.digest);
    const proposal = JSON.parse(value.proposal);
    requireValue(proposal.version === 1 && record(proposal.workspace) && Array.isArray(proposal.operations) &&
        proposal.operations.length === 1);
    const operation = proposal.operations[0];
    requireValue(operation.kind === "append-comment" && operation.workItemId === context.itemKey.workItemId &&
        operation.savedBenchId === context.benchId && operation.expectedIdentityId === context.identityId &&
        operation.expectedBenchContentsDigest === context.benchContentsDigest &&
        operation.text === text && operation.format === format && typeof operation.effectiveBody === "string");
    return { ...structuredClone(value), operation, workspace: proposal.workspace };
}
export function createNativeDiscussionPoster(options = {}) {
    const { realpath = nodeRealpath } = options;
    const invoke = createNativeCommandRunner(options);
    const readReview = createNativeProposalReader(options);
    return {
        async prepare({ context, text, format, workspacePath, signal }) {
            const cwd = await realpath(workspacePath);
            requireValue(cwd === context.itemKey.checkoutRoot);
            const native = await invoke("discussion-prepare-post",
                ["bench", "discussion", "prepare-post", "--stdin", "-o", "json"], cwd, signal,
                JSON.stringify({ version: 1, context, text, format }), { context, text, format }, validateDiscussionPreparation);
            const twigDir = await realpath(path.join(cwd, ".twig"));
            requireValue(inside(cwd, twigDir));
            const directory = path.join(twigDir, "canvas-discussion");
            await mkdir(directory, { recursive: true });
            const physical = await realpath(directory);
            requireValue(inside(twigDir, physical));
            const file = path.join(physical, randomUUID() + ".json");
            signal?.throwIfAborted();
            await writeFile(file, native.proposal, { flag: "wx", encoding: "utf8", mode: 0o600 });
            const observation = await readReview({ workspacePath: cwd, signal,
                input: { file, digest: native.digest, workspace: native.workspace } });
            const envelope = observation.retained.envelope;
            const model = envelope.reviewModel;
            const operation = model.operations[0];
            requireValue(model.modelVersion === 2 && model.operations.length === 1 &&
                operation.opId === native.operation.id && operation.kind === "append-comment" &&
                operation.target.workItemId === context.itemKey.workItemId);
            const consequence = operation.consequences.find(value => value.kind === "discussion-append");
            for (const [kind, value] of [["savedBenchId", context.benchId],
                ["expectedBenchContentsDigest", context.benchContentsDigest], ["expectedIdentityId", context.identityId]])
                requireValue(operation.preconditions.some(precondition => precondition.kind === kind && precondition.value === value));
            requireValue(consequence?.submittedText === text && consequence.bodyFormat === format &&
                consequence.effectiveBody === native.operation.effectiveBody);
            return { observation, digest: native.digest, target: structuredClone(context.itemKey),
                bench: context.benchName, identity: context.identityId, workspace: native.workspace,
                text, format, effectiveBody: native.operation.effectiveBody,
                canApply: envelope.canApply && !envelope.issues.length && !envelope.pendingChanges.length && !model.blockers.length,
                preconditions: operation.preconditions,
                blockers: [...envelope.issues, ...envelope.pendingChanges, ...model.blockers] };
        },
        async apply({ prepared, confirm, workspacePath, signal }) {
            const { observation } = prepared;
            requireValue(confirm === prepared.digest && confirm === observation.expected.digest && prepared.canApply);
            const cwd = await realpath(workspacePath);
            const file = await realpath(observation.acquisition.file);
            requireValue(cwd === observation.acquisition.checkoutRoot && file === observation.acquisition.file);
            const result = await invoke("proposal-apply",
                ["proposal", "apply", "--file", file, "--confirm", confirm, "-o", "json"], cwd, signal, undefined,
                { digest: confirm, operations: observation.retained.envelope.reviewModel.operations }, validateApplyResult);
            if (result.status !== "verified") return result;
            const receipt = JSON.parse(result.native.operations[0].resultJson);
            requireValue(receipt.workItemId === prepared.target.workItemId && /^[1-9][0-9]*$/.test(receipt.commentId) &&
                typeof receipt.commentId === "string" && receipt.body === prepared.effectiveBody && receipt.format === "html");
            return { ...result, commentId: receipt.commentId };
        },
    };
}
