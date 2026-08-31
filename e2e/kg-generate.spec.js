//Knowledge-graph generation, end to end. The AI provider is faked by intercepting the browser's own
//call to api.anthropic.com with page.route — the whole app-side pipeline (prompt, HTTP, parse, fold,
//preview, stage, render) runs for real, and no tokens are spent. The merge round-trip against a live
//Gremlin server is opt-in via GREMLIN_E2E_HOST, matching expansion-live.spec.js; it commits three
//vertices with kg-e2e-* ids to the dev graph (rerun gremlin-load-sample.ps1 to reset).
const { test, expect } = require('@playwright/test');
const { gotoApp, cyPositions, waitForStableLayout } = require('./helpers');

const host = process.env.GREMLIN_E2E_HOST;
let port = process.env.GREMLIN_E2E_PORT;
if (!port)
    port = '8182';

//Ids are stamped per run — the live test commits them to a stateful dev server, so fixed ids would
//collide with residue from a previous run of this very spec. They are also **numeric**: the dev
//TinkerGraph runs a Long id manager and rejects string ids with code 597 ("Expected an id that is
//convertible to class java.lang.Long"), while FormatId emits digit-only ids as bare numeric literals
//the server accepts. Real model output uses string ids ("acme") and cannot commit to such a server —
//a server-config limitation recorded in the spec's risks, not something this test can fix.
const RUN = Date.now();

//First generation: Alice works at Acme.
const REPLY_ONE = JSON.stringify({
    nodes: [
        { id: `${RUN}1`, label: 'Company', properties: { name: 'Acme' } },
        { id: `${RUN}2`, label: 'Person', properties: { name: 'Alice' } }
    ],
    edges: [{ source: `${RUN}2`, target: `${RUN}1`, label: 'worksAt', properties: {} }]
});

//Second generation names the same company under a variant surface form — the merge must fold it.
const REPLY_TWO = JSON.stringify({
    nodes: [
        { id: `${RUN}3`, label: 'Company', properties: { name: 'Acme Inc.' } },
        { id: `${RUN}4`, label: 'Person', properties: { name: 'Bob' } }
    ],
    edges: [{ source: `${RUN}4`, target: `${RUN}3`, label: 'worksAt', properties: {} }]
});

const CORS = {
    'Access-Control-Allow-Origin': '*',
    'Access-Control-Allow-Headers': '*',
    'Access-Control-Allow-Methods': '*'
};

//Intercepts the Anthropic Messages API: each POST returns the next reply from the list. The handler
//also answers the CORS preflight, since the fulfilled response never reaches a real server.
async function routeFakeProvider(page, replies, delayMs) {
    let call = 0;

    await page.route('https://api.anthropic.com/**', async route => {
        if (route.request().method() === 'OPTIONS') {
            await route.fulfill({ status: 204, headers: CORS });
            return;
        }

        if (delayMs)
            await new Promise(resolve => setTimeout(resolve, delayMs));

        let reply = replies[call];
        if (call < replies.length - 1)
            call++;

        const body = JSON.stringify({ content: [{ type: 'text', text: reply }] });
        await route.fulfill({ status: 200, headers: { ...CORS, 'Content-Type': 'application/json' }, body });
    });
}

//Saves a throwaway Anthropic model so the Generate button enables, then opens the ✨ modal and picks it.
//Models are kept under Settings now — this panel only chooses one — so that is where it is added. The
//API key never leaves the browser: the provider call is intercepted.
async function openModalWithModel(page) {
    const panel = '.gdbv-dialog:not(.d-none)';

    await page.getByRole('button', { name: /Settings/ }).click();
    await page.getByRole('button', { name: 'AI settings', exact: true }).click();
    await expect(page.locator(panel)).toBeVisible();

    await page.locator(panel).getByTitle('Add an AI model').click();
    await page.locator(panel).getByPlaceholder('e.g. Claude Opus').fill('Fake');
    await page.locator(panel).locator('input[type="password"]').fill('not-a-real-key');
    await page.locator(panel).getByRole('button', { name: 'Add', exact: true }).click();

    await page.locator(panel + ' .btn-close').click();
    await expect(page.locator(panel)).toHaveCount(0);

    await page.getByRole('button', { name: /Generate with AI/ }).click();

    const modal = page.locator(panel);
    await expect(modal.getByText('Generate a knowledge graph')).toBeVisible();

    //Not necessarily the only model on offer: a Development build seeds whatever dev-secrets.json holds.
    await modal.locator("select[title^='Which saved AI model']").selectOption('Fake');

    return modal;
}

async function generate(modal, sourceText) {
    await modal.getByPlaceholder(/Paste notes/).fill(sourceText);
    await modal.getByRole('button', { name: 'Generate graph', exact: true }).click();
}

test('generate → replace renders the graph offline and stages it', async ({ page }) => {
    await gotoApp(page);
    await routeFakeProvider(page, [REPLY_ONE]);

    const modal = await openModalWithModel(page);
    await generate(modal, 'Alice works at Acme.');

    //The preview is the review gate: counts, breakdown, and the exact Gremlin.
    await expect(modal.getByText('2 node(s) · 1 edge(s)')).toBeVisible();
    await expect(modal.getByText(/Person ×1/)).toBeVisible();

    await modal.getByRole('button', { name: /Use this graph/ }).click();

    //Accepting renders offline (no database anywhere) and stages the script.
    await expect(page.locator('#cyGraph canvas').first()).toBeVisible();
    await waitForStableLayout(page);

    expect(await cyPositions(page)).toHaveLength(2);
});

//The spec's worry — an in-flight generation destroyed by the panel closing underneath it — used to be
//answered by the backdrop, which simply blocked the click. The window does not block anything now, so
//the guarantee has to come from where it is rendered: the dialog is a sibling of the import panel in
//Home, not a child of it, and closing the panel therefore cannot unmount it. This closes the panel
//mid-generation and proves the answer arrives anyway.
test('a slow generation survives the panel beneath it closing', async ({ page }) => {
    await gotoApp(page);
    await routeFakeProvider(page, [REPLY_ONE], 1200);

    const modal = await openModalWithModel(page);
    await generate(modal, 'Alice works at Acme.');

    //Reachable now, and pressed while the request is still out.
    await page.getByRole('button', { name: /Import \/ Export/ }).click();

    await expect(modal.getByText('2 node(s) · 1 edge(s)')).toBeVisible({ timeout: 15000 });
});

test('merge folds the shared entity to one node and commits without an id collision', async ({ page }) => {
    test.skip(!host, 'set GREMLIN_E2E_HOST to run against a live, seeded Gremlin server');

    await gotoApp(page);
    await routeFakeProvider(page, [REPLY_ONE, REPLY_TWO]);

    //First document, Replace: two nodes on an offline canvas.
    let modal = await openModalWithModel(page);
    await generate(modal, 'Alice works at Acme.');
    await expect(modal.getByText('2 node(s) · 1 edge(s)')).toBeVisible();
    await modal.getByRole('button', { name: /Use this graph/ }).click();
    await expect(page.locator('#cyGraph canvas').first()).toBeVisible();
    await waitForStableLayout(page);
    expect(await cyPositions(page)).toHaveLength(2);

    //Second document names the same company. Merge previews the post-fold result…
    await page.getByRole('button', { name: /Import \/ Export/ }).click();
    modal = page.locator('.gdbv-dialog:not(.d-none)');
    await page.getByRole('button', { name: /Generate with AI/ }).click();
    await generate(modal, 'Bob also works at Acme Inc.');
    await modal.getByRole('button', { name: 'Merge into drawing', exact: true }).click();
    await expect(modal.getByText(/1 merged into existing/)).toBeVisible();
    await modal.getByRole('button', { name: /Use this graph/ }).click();

    //…and the canvas shows ONE Acme: three nodes, not four.
    await waitForStableLayout(page);
    expect(await cyPositions(page)).toHaveLength(3);

    //Connect to the live server and commit the staged buffer — the T.id proof: a colliding addV
    //would fail the commit and leave the buffer (and an error) behind. The top bar reads "Offline
    //mode" here (ba54f4b), and clicking it opens the connection card.
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

    //Success clears the buffer — the Commit button disables and the Generated tab shows its empty
    //placeholder. Any per-line failure (a duplicate id above all) keeps the failing buffer staged,
    //so the button would stay enabled and this trace would show it. (The textual "N query(ies)
    //committed." status renders only in the element-properties sidebar, which this flow never opens.)
    await expect(commit).toBeDisabled({ timeout: 20000 });
    await expect(page.getByText(/Queries generated from property changes will appear here/)).toBeVisible();
});

//The three ways into the source box stand side by side, and the middle one takes a dropped file. The
//panel is only what you see: a native file input stretched invisibly over it is what catches the file,
//which is why dropping and clicking both work without either being wired up separately.
test('a file dropped on the panel loads its text', async ({ page }) => {
    await gotoApp(page);
    await page.getByRole('button', { name: /Generate with AI/ }).click();

    const modal = page.locator('.gdbv-dialog:not(.d-none)');
    await expect(modal).toBeVisible();

    const laidOut = await modal.evaluate(p => {
        const box = el => el.getBoundingClientRect();
        const text = box(p.querySelector('.gdbv-source-text'));
        const zone = box(p.querySelector('.gdbv-dropzone'));
        const input = box(p.querySelector('.gdbv-dropzone-input'));
        const wiki = box(p.querySelector("input[placeholder*='Wikipedia']"));

        return {
            inOrder: text.x < zone.x && zone.x < wiki.x,
            onOneRow: Math.abs(text.y - zone.y) < 4,
            //If the input stops covering the panel, dropping silently stops working while clicking still does.
            inputCoversPanel: Math.abs(input.width - zone.width) < 3 && Math.abs(input.height - zone.height) < 3
        };
    });

    expect(laidOut.inOrder).toBe(true);
    expect(laidOut.onOneRow).toBe(true);
    expect(laidOut.inputCoversPanel).toBe(true);

    //A dropped file arrives at that same input, so setting it exercises the path a drop takes.
    await modal.locator('.gdbv-dropzone-input').setInputFiles({
        name: 'notes.txt',
        mimeType: 'text/plain',
        buffer: Buffer.from('Alice works at Acme. Bob knows Alice.')
    });

    await expect(modal.locator('.gdbv-source-text')).toHaveValue(/Alice works at Acme/, { timeout: 10000 });

    //And it says which file it read, since the input that read it cannot be seen.
    await expect(modal).toContainText('notes.txt');
});

//A real PDF, dropped on the panel. This is the only test that proves the lazy-loaded reader works: the
//PdfPig assemblies are kept out of the boot manifest (BlazorWebAssemblyLazyLoad in the host's project
//file), so nothing touches them until this path runs and LazyPdfReaderLoader fetches them. A bUnit test
//cannot show that — it runs on a CLR where every assembly is already present.
test('a dropped PDF loads its text, fetching the reader on demand', async ({ page }) => {
    const framework = [];
    page.on('request', r => { if (r.url().includes('_framework/')) framework.push(r.url()); });

    await gotoApp(page);
    await page.getByRole('button', { name: /Generate with AI/ }).click();

    const modal = page.locator('.gdbv-dialog:not(.d-none)');
    await expect(modal).toBeVisible();

    //Nothing has asked for a PDF yet, so the reader must not have been downloaded.
    expect(framework.filter(u => u.includes('PdfPig'))).toHaveLength(0);

    await modal.locator('.gdbv-dropzone-input').setInputFiles(require('path').join(__dirname, 'assets', 'sample.pdf'));

    await expect(modal.locator('.gdbv-source-text')).toHaveValue(/Acme Robotics/, { timeout: 30000 });
    await expect(modal.locator('.gdbv-source-text')).toHaveValue(/Vision team/);
    await expect(modal).toContainText('sample.pdf');

    //And it was fetched, rather than having been in the boot payload all along.
    expect(framework.filter(u => u.includes('PdfPig')).length).toBeGreaterThan(0);
});
