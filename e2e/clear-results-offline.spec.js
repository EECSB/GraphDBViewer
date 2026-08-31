//"Clear results" must empty the canvas with no database connection, as long as there is something to clear.
//Regression: ClearResultsAsync treated "disconnected && !reflectDbState" as "the drawing IS the staged
//edits" and merely discarded the staged queries. reflectDbState is persisted and any edit (or the Preview
//toggle) turns it off, so a restored/drawn graph with nothing staged landed in that branch, discarded
//nothing and returned — the button looked dead until you connected to a database.
const { test, expect } = require('@playwright/test');
const { gotoApp, loadSampleGraph, SAMPLE_NODE_COUNT } = require('./helpers');

async function nodeCount(page) {
    return await page.evaluate(() => JSON.parse(getCytoscapePositions()).length);
}

test('Clear results empties the canvas while disconnected with nothing staged', async ({ page }) => {
    await gotoApp(page);
    await loadSampleGraph(page);
    expect(await nodeCount(page)).toBe(SAMPLE_NODE_COUNT);

    //Previewing staged edits is what turns reflectDbState off — the persisted flag that sent the old code
    //down the discard-only path. The DOT import also stages generated queries, so drop those: that leaves
    //reflectDbState off with nothing staged, which is the state the button died in (and the state you
    //reopen the app in after committing or discarding earlier work).
    await page.getByLabel('Preview data changes').check();
    page.once('dialog', d => d.accept());
    await page.getByRole('button', { name: 'Discard changes' }).click();
    await expect(page.getByRole('button', { name: 'Discard changes' })).toBeHidden();

    //The imported graph is still on the canvas, so there is genuinely something to clear.
    expect(await nodeCount(page)).toBeGreaterThan(0);

    //No database was ever connected in this spec, so clearing has to work purely offline.
    page.once('dialog', d => d.accept());
    await page.getByTitle(/^Clear results/).click();

    await expect.poll(async () => await nodeCount(page), { timeout: 15000 }).toBe(0);
});
