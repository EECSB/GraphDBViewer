//Knowledge-graph generation, end to end. Ask AI's since 2026-11-02, at the user's word to reuse that window rather than keep
//one of its own: the Import panel's Generate with AI opens Ask AI with a graph asked for, the model proposes a graph by a tool
//call, and the call's card is the preview and Use. The AI provider is faked by intercepting the browser's own call to
//api.anthropic.com with page.route, so the whole app-side path (instructions, tool call, parse, fold, preview, stage, render)
//runs for real and no tokens are spent. The merge round-trip against a live Gremlin server is opt-in via GREMLIN_E2E_HOST,
//matching expansion-live.spec.js; it commits four vertices to the dev graph and **sweeps them again afterwards**, so a shared
//server is left as it was found.
//
//What the window's own specs walked and Ask AI now does elsewhere: a file dropped is an attachment (AskAiFilesTests), and the
//extraction options are the card's choices and the model's instructions (AskAiGraphTests, KgPromptTests). The lazy PDF reader
//this spec proved loads on demand had the window as its one caller in Treeality.
const { test, expect } = require('@playwright/test');
const { gotoApp, openAccountMenu, openImportPanel, cyPositions, waitForStableLayout } = require('./helpers');

const host = process.env.GREMLIN_E2E_HOST;
let port = process.env.GREMLIN_E2E_PORT;
if (!port)
    port = '8182';

//Ids are stamped per run — the live test commits them to a stateful dev server, so fixed ids would collide with residue from
//a previous run of this very spec. They are also **numeric**: a server with a Long id manager rejects string ids with code 597,
//while FormatId emits digit-only ids as bare numeric literals the server accepts.
const RUN = Date.now();

//First graph: Alice works at Acme.
const GRAPH_ONE = {
    nodes: [
        { id: `${RUN}1`, label: 'Company', properties: { name: 'Acme' } },
        { id: `${RUN}2`, label: 'Person', properties: { name: 'Alice' } }
    ],
    edges: [{ source: `${RUN}2`, target: `${RUN}1`, label: 'worksAt', properties: {} }]
};

//Second graph names the same company under a variant surface form — the merge must fold it.
const GRAPH_TWO = {
    nodes: [
        { id: `${RUN}3`, label: 'Company', properties: { name: 'Acme Inc.' } },
        { id: `${RUN}4`, label: 'Person', properties: { name: 'Bob' } }
    ],
    edges: [{ source: `${RUN}4`, target: `${RUN}3`, label: 'worksAt', properties: {} }]
};

const CORS = {
    'Access-Control-Allow-Origin': '*',
    'Access-Control-Allow-Headers': '*',
    'Access-Control-Allow-Methods': '*'
};

const panel = '.gdbv-dialog:not(.d-none)';
const ask = '.gdbv-ai-window:not(.d-none)';
const promptBox = ask + ' [data-testid="ai-prompt"]';
const cards = ask + ' [data-tool="propose_graph"]';

const usage = { input_tokens: 100, output_tokens: 10 };

function says(text) {
    return { content: [{ type: 'text', text }], stop_reason: 'end_turn', usage };
}

let calls = 0;

function proposes(graph) {
    calls++;

    return { content: [{ type: 'tool_use', id: `call-${calls}`, name: 'propose_graph', input: graph }], stop_reason: 'tool_use', usage };
}

//Drops the four vertices the live test commits, over the viewer's own query box. Its ids are this run's, so nothing else on a
//shared dev server is touched, and it runs whether or not the test reached the commit.
async function sweep(page) {
    const ids = [`${RUN}1`, `${RUN}2`, `${RUN}3`, `${RUN}4`].join(',');
    const query = `g.V(${ids}).drop()`;

    await page.goto(`/?host=${host}&port=${port}&ssl=false&query=${encodeURIComponent(query)}`);

    //Wait for the CONNECTION, not merely for the app to boot: the toolbar appears well before the URL's connection is up and
    //its query has run, and a sweep that misses its own vertices reports nothing.
    await expect(page.getByRole('button', { name: /Connected/ })).toBeVisible({ timeout: 60000 });

    //And the query still lands a moment after the connection does.
    await page.waitForTimeout(4000);
}

test.afterEach(async ({ page }, testInfo) => {
    //Only the live test writes anything; the offline one shares this file and has nothing to clean.
    if (!host || !testInfo.title.includes('commits without an id collision'))
        return;

    try {
        await sweep(page);
    }
    catch { }
});

//Everything that is not this app, the faked provider or the dev server is refused outright: a Development build seeds
//whatever models dev-secrets.json holds, and picking a real one by mistake would spend a real key.
async function blockRealProviders(page) {
    await page.route('**', route => {
        const url = route.request().url();

        if (url.startsWith('http://localhost') || url.startsWith('https://localhost') || url.startsWith('https://api.anthropic.com') || (host && url.includes(host))) {
            route.fallback();
            return;
        }

        route.abort('blockedbyclient');
    });
}

//Each POST returns the next reply, so a test scripts a conversation: a proposal, then the words after it.
async function routeFakeProvider(page, replies) {
    let call = 0;

    await page.route('https://api.anthropic.com/**', async route => {
        if (route.request().method() === 'OPTIONS') {
            await route.fulfill({ status: 204, headers: CORS });
            return;
        }

        const reply = replies[Math.min(call, replies.length - 1)];
        call++;

        await route.fulfill({ status: 200, headers: { ...CORS, 'Content-Type': 'application/json' }, body: JSON.stringify(reply) });
    });
}

//A throwaway model, so Send enables. The key reaches no provider: the call is intercepted.
async function addModel(page) {
    await openAccountMenu(page);
    await page.getByRole('button', { name: 'AI settings', exact: true }).click();
    await expect(page.locator(panel)).toBeVisible();

    await page.locator(panel).getByTestId('llm-picker-chevron').click();
    await page.locator(panel).getByTitle('Add an AI model').click();
    await page.locator(panel).getByPlaceholder('e.g. Claude Opus').fill('Fake');
    await page.locator(panel).locator('input[type="password"]').fill('not-a-real-key');
    await page.locator(panel).getByTestId('llm-form-save').click();

    await page.locator(panel + ' .btn-close').click();
    await expect(page.locator(panel)).toHaveCount(0);
}

//Opens Ask AI the way the Import panel's ✨ button does, with its wizard in the conversation, and picks the fake model.
async function openFromImport(page) {
    await openImportPanel(page);
    await page.getByTestId('kg-generate').click();

    await expect(page.locator(ask)).toBeVisible();
    await expect(page.locator(ask).getByTestId('kg-wizard')).toBeVisible();

    await page.locator(ask).getByTestId('ai-model-toggle').click();
    await page.locator(ask + ' [data-model="Fake"]').click();
    await expect(page.locator(ask).getByTestId('ai-model-chosen')).toHaveText('Fake');
}

async function send(page, text) {
    await page.locator(promptBox).fill(text);
    await page.locator(ask).getByTestId('ai-send').click();
}

test('The wizard takes the text and options through to a proposed graph in the conversation', async ({ page }) => {
    await blockRealProviders(page);
    await gotoApp(page);
    await routeFakeProvider(page, [proposes(GRAPH_ONE), says('Two entities, and who works where.')]);
    await addModel(page);
    await openFromImport(page);

    const wizard = page.locator(ask).getByTestId('kg-wizard');

    await expect(wizard.getByTestId('ai-wizard-next')).toBeDisabled();
    await wizard.getByTestId('kg-source-text').fill('Alice works at Acme.');
    await wizard.getByTestId('ai-wizard-next').click();
    await wizard.getByTestId('kg-focus').fill('people');
    await wizard.getByTestId('kg-wizard-confidence').check();
    await wizard.getByTestId('ai-wizard-next').click();

    await expect(wizard.getByTestId('kg-review')).toContainText('Rate confidence');
    await expect(wizard.getByTestId('kg-review-model')).toHaveText('Fake');

    await wizard.getByTestId('ai-wizard-finish').click();

    await expect(wizard).toHaveCount(0);
    await expect(page.locator(ask).getByTestId('ai-transcript')).toContainText('Look for people.');
    await expect(page.locator(cards).first().getByTestId('kg-counts')).toHaveText('2 node(s) · 1 edge(s)');
});

test('Generate with AI opens Ask AI, and a proposed graph is drawn offline when used', async ({ page }) => {
    await blockRealProviders(page);
    await gotoApp(page);
    await routeFakeProvider(page, [proposes(GRAPH_ONE), says('Two entities, and who works where.')]);
    await addModel(page);
    await openFromImport(page);

    await send(page, 'Draw a knowledge graph of this text: Alice works at Acme.');

    //The card is the review gate: counts, breakdown, the exact script, and the choices before Use.
    const card = page.locator(cards).first();

    await expect(card.getByTestId('kg-counts')).toHaveText('2 node(s) · 1 edge(s)');
    await expect(card.getByTestId('kg-labels')).toContainText('Person ×1');
    await expect(card.getByTestId('kg-script')).toContainText("addV('Company')");
    await expect(card.getByTestId('kg-orphans')).not.toBeChecked();
    await expect(page.locator(ask)).toContainText('Two entities, and who works where.');

    await card.getByTestId('kg-use').click();

    //Using it renders offline (no database anywhere) and stages the script.
    await expect(page.locator('#cyGraph canvas').first()).toBeVisible();
    await waitForStableLayout(page);

    expect(await cyPositions(page)).toHaveLength(2);
});

test('merge folds the shared entity to one node and commits without an id collision', async ({ page }) => {
    test.skip(!host, 'set GREMLIN_E2E_HOST to run against a live, seeded Gremlin server');

    //As the administrator: the commit goes through the host's route, which since connections.route is a permission the seeded
    //"user" role does not hold.
    await blockRealProviders(page);
    await gotoApp(page, 'administrator');
    await routeFakeProvider(page, [proposes(GRAPH_ONE), says('First.'), proposes(GRAPH_TWO), says('Second.')]);
    await addModel(page);
    await openFromImport(page);

    //First text, Replace: two nodes on an offline canvas.
    await send(page, 'Draw a knowledge graph of this text: Alice works at Acme.');

    const first = page.locator(cards).nth(0);

    await expect(first.getByTestId('kg-counts')).toHaveText('2 node(s) · 1 edge(s)');
    await first.getByTestId('kg-use').click();
    await expect(page.locator('#cyGraph canvas').first()).toBeVisible();
    await waitForStableLayout(page);
    expect(await cyPositions(page)).toHaveLength(2);

    //Second text names the same company. Merge previews the post-fold result…
    await send(page, 'And this one: Bob also works at Acme Inc.');

    const second = page.locator(cards).nth(1);

    await second.getByTestId('kg-merge').click();
    await expect(second.getByTestId('kg-counts')).toContainText('1 merged into existing');
    await second.getByTestId('kg-use').click();

    //…and the canvas shows ONE Acme: three nodes, not four.
    await waitForStableLayout(page);
    expect(await cyPositions(page)).toHaveLength(3);

    //Out of the way of the workspace's row, then connect to the live server and commit the staged buffer — the T.id proof: a
    //colliding addV would fail the commit and leave the buffer (and an error) behind.
    await page.locator(ask).getByRole('button', { name: 'Close', exact: true }).first().click();
    await page.getByRole('button', { name: /Offline mode/ }).click();

    //The host/port labels aren't for-associated with their inputs, so locate by adjacency.
    await page.locator('label:has-text("Hostname") + input').fill(host);
    await page.locator('label:has-text("Port") + input').fill(port);
    await page.getByLabel(/SSL/).uncheck();
    await page.getByRole('button', { name: 'Connect', exact: true }).click();
    await expect(page.getByRole('button', { name: /Connected/ })).toBeVisible({ timeout: 20000 });

    const commit = page.getByRole('button', { name: /Commit Changes/ });
    await expect(commit).toBeEnabled();
    await commit.click();

    //Success clears the buffer, so the Commit button disables; any per-line failure keeps the failing buffer staged.
    await expect(commit).toBeDisabled({ timeout: 20000 });

    //And the Generated tab says so in its own words: the success text, which a commit that failed every line would not show.
    const status = page.locator('.text-success', { hasText: /query\(ies\) committed/ });

    await expect(status).toBeVisible({ timeout: 20000 });
});
