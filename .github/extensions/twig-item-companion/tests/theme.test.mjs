import assert from "node:assert/strict";
import { Script, createContext } from "node:vm";
import test from "node:test";
import { renderPage } from "../render.mjs";

function client({ host, osDark = false } = {}) {
    const nodes = new Map();
    const element = () => ({
        dataset: {}, listeners: {}, textContent: "", value: "",
        classList: { add() {}, remove() {} },
        addEventListener(event, callback) { this.listeners[event] = callback; },
        getAttribute(name) { return name === "data-color-mode" ? this.dataset.colorMode ?? null : null; },
    });
    const root = element();
    const body = element();
    if (host) root.dataset.colorMode = host;
    const media = { matches: osDark, addEventListener(event, callback) { this.changed = callback; } };
    const observers = [];
    const document = {
        documentElement: root, body,
        querySelector(selector) {
            if (!nodes.has(selector)) nodes.set(selector, element());
            return nodes.get(selector);
        },
    };
    const context = createContext({
        document,
        window: { matchMedia: () => media },
        MutationObserver: class {
            constructor(callback) { observers.push(callback); }
            observe() {}
        },
        fetch: async () => ({ ok: true, json: async () => ({ busy: false }) }),
    });
    const html = renderPage({ token: "fixture-token", nonce: "fixture-nonce" });
    const source = html.match(/<script nonce="fixture-nonce">([\s\S]*?)<\/script>/)[1];
    new Script(source).runInContext(context);
    return {
        root, body, media, html, nodes,
        select(mode) {
            const select = nodes.get("#theme-mode");
            select.value = mode;
            select.listeners.change();
        },
        hostChanged(mode) { root.dataset.colorMode = mode; observers.forEach((callback) => callback()); },
    };
}

test("Auto uses OS dark fallback without changing host attributes", () => {
    const ui = client({ osDark: true });
    assert.equal(ui.nodes.get("#theme-mode").value, "auto");
    assert.equal(ui.body.dataset.twigTheme, "auto");
    assert.equal(ui.body.dataset.twigColorMode, "dark");
    assert.equal(ui.root.dataset.colorMode, undefined);
    assert.match(ui.nodes.get("#theme-status").textContent, /Dark.*system/i);
    assert.match(ui.html, /<label for="theme-mode">Theme/);
    assert.match(ui.html, /<option value="auto">Auto<\/option>/);
    assert.match(ui.html, /<option value="light">Light<\/option>/);
    assert.match(ui.html, /<option value="dark">Dark<\/option>/);
});

test("Auto follows host mode before OS and reacts to host and OS changes", () => {
    const ui = client({ host: "light", osDark: true });
    assert.equal(ui.body.dataset.twigColorMode, "light");
    ui.hostChanged("dark");
    assert.equal(ui.body.dataset.twigColorMode, "dark");
    ui.hostChanged("auto");
    ui.media.matches = false;
    ui.media.changed();
    assert.equal(ui.body.dataset.twigColorMode, "light");
});

test("explicit overrides survive host and OS changes until Auto is selected", () => {
    const ui = client({ host: "dark", osDark: true });
    ui.select("light");
    assert.equal(ui.body.dataset.twigTheme, "light");
    assert.equal(ui.body.dataset.twigColorMode, "light");
    ui.hostChanged("dark");
    ui.media.changed();
    assert.equal(ui.body.dataset.twigColorMode, "light");
    assert.equal(ui.root.dataset.colorMode, "dark");
    ui.select("dark");
    assert.equal(ui.body.dataset.twigColorMode, "dark");
    ui.hostChanged("light");
    assert.equal(ui.body.dataset.twigColorMode, "dark");
    ui.select("auto");
    assert.equal(ui.body.dataset.twigColorMode, "light");
});

test("theme control and fallback palette include accessible keyboard focus and explicit dark colors", () => {
    const ui = client();
    assert.match(ui.html, /select:focus-visible/);
    assert.match(ui.html, /summary:focus-visible/);
    assert.match(ui.html, /body\[data-twig-palette="dark"\]/);
    assert.match(ui.html, /color-scheme: dark/);
    assert.match(ui.html, /--twig-background: #0d1117/);
    assert.match(ui.html, /--twig-text: #f0f6fc/);
});

test("fallback text exceeds 4.5:1 and focus/borders exceed 3:1 in both modes", () => {
    const html = client().html;
    function luminance(hex) {
        const rgb = hex.match(/\w\w/g).map((part) => parseInt(part, 16) / 255)
            .map((value) => value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4);
        return rgb[0] * 0.2126 + rgb[1] * 0.7152 + rgb[2] * 0.0722;
    }
    for (const selector of ["body", 'body[data-twig-palette="dark"]']) {
        const block = html.slice(html.indexOf(selector + " {")).split("}")[0];
        const colors = Object.fromEntries([...block.matchAll(/--twig-(\w+): #([0-9a-f]{6})/g)]
            .map((match) => [match[1], match[2]]));
        for (const [role, threshold] of [["text", 4.5], ["muted", 4.5], ["focus", 3], ["border", 3], ["error", 3]]) {
            const levels = [luminance(colors[role]), luminance(colors.background)].sort((a, b) => a - b);
            const contrast = (levels[1] + 0.05) / (levels[0] + 0.05);
            assert.ok(contrast >= threshold, `${selector} ${role}: ${contrast} < ${threshold}`);
        }
    }
});
