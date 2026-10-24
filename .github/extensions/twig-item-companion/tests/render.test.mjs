import assert from "node:assert/strict";
import { Script } from "node:vm";
import test from "node:test";
import { renderPage } from "../render.mjs";

test("renders a nonce-protected client script without executable untrusted markup", () => {
    const html = renderPage({ token: "instance-token", nonce: "test-nonce" });
    const script = html.match(/<script nonce="test-nonce">([\s\S]*?)<\/script>/)?.[1];

    assert.ok(script);
    assert.doesNotThrow(() => new Script(script));
    assert.match(html, /Read-only semantic detail/);
    assert.match(html, /Editing/);
    assert.match(html, /Related-item coverage/);
    assert.doesNotMatch(script, /\.innerHTML\b|eval\s*\(/);
});
