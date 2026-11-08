//Node-image overlays: a per-label Icon URL set in the Style dialog lands on the matching
//nodes — as image data in the 2D Cytoscape elements and as positioned <img> overlays in 3D.
const { test, expect } = require('@playwright/test');
const {
    gotoApp,
    loadSampleGraph,
    switchTo3d,
    waitForStableLayout,
    TINY_PNG,
    SAMPLE_NODE_COUNT,
    SAMPLE_PERSON_COUNT
} = require('./helpers');

test('a per-label icon shows on that label\'s nodes in 2D and 3D', async ({ page }) => {
    await gotoApp(page);
    await loadSampleGraph(page);

    //Set an icon URL for the person label in the Style dialog (offline data: URL image).
    await page.getByRole('button', { name: /Style: set each vertex label/ }).click();
    const dialog = page.locator('.gdbv-style-window');

    //The icon is under a label's row, opened by its arrow (2026-11-04, the Style window in tabs).
    await dialog.locator('[data-testid=style-row][data-label=person] [data-testid=style-row-toggle]').click();

    const personMore = dialog.locator('[data-testid=style-row-more][data-label=person]');
    await personMore.locator('input[title^="Image URL"]').fill(TINY_PNG);
    await personMore.locator('input[title^="Image URL"]').press('Enter');
    await dialog.locator('.btn-close').click();
    await expect(dialog).toBeHidden();

    //2D: person nodes carry the image in their element data, company nodes don't.
    await waitForStableLayout(page);
    await expect.poll(async () => {
        const info = await page.evaluate(() => getCytoscapeElementInfo('Alice'));

        return info.data.image;
    }).toBe(TINY_PNG);

    const company = await page.evaluate(() => getCytoscapeElementInfo('Acme'));
    expect(company.data.image).toBeUndefined();

    //3D: one <img> overlay per person node, pinned over the canvas.
    await switchTo3d(page, SAMPLE_NODE_COUNT);
    await expect(page.locator('#graph3d img')).toHaveCount(SAMPLE_PERSON_COUNT);
    await expect(page.locator('#graph3d img').first()).toHaveAttribute('src', TINY_PNG);
});
