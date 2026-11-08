//Delete mode (click a component to stage its drop): clicking one of several shift/box-selected components
//stages a drop for the WHOLE highlighted group, not just the clicked one. Loaded offline (DOT import) with
//staged edits previewed on the canvas, so the deletions show without a database.
//
//The tap is driven through Cytoscape's own event emit rather than a pixel click: it runs the app's real
//`tap` node handler (which captures the highlighted group into the click payload) without depending on
//node coordinates — the offline force layout can stall with every node stacked at the origin.
const { test, expect } = require('@playwright/test');
const { gotoApp, loadSampleGraph, expectCanvasHolds, waitForStableLayout } = require('./helpers');

test.beforeEach(async ({ page }) => {
    await gotoApp(page);
    await loadSampleGraph(page);

    //Preview staged edits on the canvas so a staged drop removes the element right away.
    await page.getByLabel('Preview data changes').check();
    await waitForStableLayout(page);
});

test('clicking one of several highlighted nodes deletes the whole group', async ({ page }) => {
    //Delete mode: a canvas click now stages a drop instead of selecting into the property panel.
    await page.getByRole('button', { name: /Delete mode/ }).click();

    //Highlight a group (the same :selected end-state a shift / box-select produces), then tap one member.
    await page.evaluate(() => {
        cy.elements().unselect();
        cy.getElementById('Alice').select();
        cy.getElementById('Bob').select();
        cy.getElementById('Alice').emit('tap');
    });

    //Both highlighted nodes (and their incident edges) are staged for deletion and — with preview on —
    //leave the canvas; the three un-highlighted nodes stay. Before the fix only the clicked node was dropped.
    await expectCanvasHolds(page, ['Acme', 'Carol', 'Globex']);
});

test('clicking a node with nothing else highlighted deletes just that node', async ({ page }) => {
    await page.getByRole('button', { name: /Delete mode/ }).click();

    await page.evaluate(() => {
        cy.elements().unselect();
        cy.getElementById('Carol').emit('tap');
    });

    await expectCanvasHolds(page, ['Acme', 'Alice', 'Bob', 'Globex']);
});
