// An admitted-executable stand-in for adapter tests: a fixed absolute path, no hashing or help probing.
export const FAKE_TWIG = process.platform === "win32" ? "C:\\fake-twig\\twig.exe" : "/fake-twig/twig";

export function fakeExecutable({ refuse } = {}) {
    const prepared = [];
    return Object.freeze({
        path: FAKE_TWIG,
        sha256: "0".repeat(64),
        canvas: "test",
        prepared,
        prepare: async (capability, { signal } = {}) => {
            signal?.throwIfAborted();
            prepared.push(capability);
            if (refuse) throw refuse;
            return FAKE_TWIG;
        },
    });
}
