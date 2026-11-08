//The Delete key in the 2D view stages a drop for EVERY shift / box-selected component, not just the
//last-clicked one that .NET tracks as the single selection. Regression for multi-select Delete doing
//nothing. Loaded offline (DOT import) with staged edits previewed on the canvas, so a delete shows
//without a database.
const { test, expect } = require('@playwright/test');
const { gotoApp, loadSampleGraph, expectCanvasHolds, waitForStableLayout } = require('./helpers');

test.beforeEach(async ({ page }) => {
    await gotoApp(page);
    await loadSampleGraph(page);

    //Preview staged edits on the canvas so a staged drop removes the element right away (the default
    //mirrors the database, where deletes only show once they are committed).
    await page.getByLabel('Preview data changes').check();
    await waitForStableLayout(page);
});

//Selects the given element ids through Cytoscape's own API — the same :selected end-state a shift /
//box-select produces — then drops focus off any input so the global Delete-key handler runs. Returns the
//ids the app reads back as selected, to prove the whole multi-selection is seen (not just the last one).
async function selectAndReadBack(page, ids) {
    return await page.evaluate(elementIds => {
        cy.elements().unselect();
        elementIds.forEach(id => cy.getElementById(id).select());
        if (document.activeElement && document.activeElement.blur)
            document.activeElement.blur();

        return JSON.parse(getCytoscapeSelectedElements()).map(e => e.id).sort();
    }, ids);
}

test('Delete removes every selected node, not just the last-clicked one', async ({ page }) => {
    expect(await selectAndReadBack(page, ['Alice', 'Bob'])).toEqual(['Alice', 'Bob']);

    await page.keyboard.press('Delete');

    //Both selected nodes (and their incident edges) are staged for deletion and — with preview on — leave
    //the canvas; the three unselected nodes stay. Before the fix, a box-select left .NET's single
    //selectedElement null, so Delete did nothing.
    await expectCanvasHolds(page, ['Acme', 'Carol', 'Globex']);
});

test('Delete with a single selected node still deletes it', async ({ page }) => {
    expect(await selectAndReadBack(page, ['Carol'])).toEqual(['Carol']);

    await page.keyboard.press('Delete');

    await expectCanvasHolds(page, ['Acme', 'Alice', 'Bob', 'Globex']);
});
