//Importing over an existing drawing overwrites the staged Generated queries, so it has to ask first —
//which is what every other destructive action in the app does (close tab, clear editor, clear results,
//discard changes, delete saved query). This path was the one exception and destroyed the buffer silently.
//
//Run offline via DOT import, which stages addV/addE without a database: the first import has nothing to
//lose and must NOT ask, and the second one must. Playwright dismisses dialogs by default, so a regression
//that reintroduces an unwanted confirm surfaces as the import silently doing nothing.
const { test, expect } = require('@playwright/test');
const { gotoApp, loadSampleGraph, openImportPanel, cyPositions, SAMPLE_NODE_COUNT } = require('./helpers');

//A second, deliberately smaller graph so a replace is unambiguous by node count (2 vs the sample's 5).
const SECOND_DOT = `digraph {
    Zeta [type=person]
    Yara [type=person]
    Zeta -> Yara [label=knows]
}`;

//Pastes DOT into the Import panel and clicks Visualize, without waiting for a render — the caller
//decides what should happen next, which for a canceled import is "nothing".
async function pasteAndVisualize(page, dot) {
    await openImportPanel(page);
    await page.getByPlaceholder(/GraphSON array/).fill(dot);
    await page.getByRole('button', { name: 'Visualize', exact: true }).click();
}

test('the first import does not ask — there is nothing staged to lose', async ({ page }) => {
    await gotoApp(page);

    let asked = false;
    page.on('dialog', async d => {
        asked = true;
        await d.accept();
    });

    //Renders, so the import went through rather than being blocked behind a dialog.
    await loadSampleGraph(page);

    expect(asked).toBe(false);
    expect((await cyPositions(page)).length).toBe(SAMPLE_NODE_COUNT);
});

test('importing over a drawing asks first, and canceling keeps the drawing', async ({ page }) => {
    await gotoApp(page);
    await loadSampleGraph(page);

    let message = null;
    page.once('dialog', async d => {
        message = d.message();
        await d.dismiss();
    });

    await pasteAndVisualize(page, SECOND_DOT);

    await expect.poll(() => message).toContain('Generated tab');

    //Canceled: the original five nodes are still there, and the staged queries with them.
    await expect.poll(async () => (await cyPositions(page)).length).toBe(SAMPLE_NODE_COUNT);
});

test('accepting the confirm replaces the drawing', async ({ page }) => {
    await gotoApp(page);
    await loadSampleGraph(page);

    page.once('dialog', async d => await d.accept());

    await pasteAndVisualize(page, SECOND_DOT);

    await expect.poll(async () => (await cyPositions(page)).length).toBe(2);
});
