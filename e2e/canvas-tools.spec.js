//2D canvas editing tools added in the icon/UX pass: grid mode (a grid overlay plus snap-to-grid)
//and the confirm-before-clear guard on the Clear-results button. All offline — the graph is loaded
//through the Import panel (DOT), so no database is needed.
const { test, expect } = require('@playwright/test');
const { gotoApp, loadSampleGraph, cyScreenPoint, SAMPLE_NODE_COUNT } = require('./helpers');

test.beforeEach(async ({ page }) => {
    await gotoApp(page);
    await loadSampleGraph(page);
});

//The inline background-image the grid overlay sets on the Cytoscape container (empty when off).
function gridBackground(page) {
    return page.evaluate(() => document.getElementById('cyGraph').style.backgroundImage);
}

//Node count currently in the 2D view, read through the interop global.
function nodeCount(page) {
    return page.evaluate(() => JSON.parse(getCytoscapePositions()).length);
}

test('grid mode draws an overlay on the canvas and toggles off again', async ({ page }) => {
    expect(await gridBackground(page)).toBe('');

    await page.getByRole('button', { name: /Grid mode/ }).click();
    await expect.poll(() => gridBackground(page)).toContain('linear-gradient');

    await page.getByRole('button', { name: /Grid mode/ }).click();
    await expect.poll(() => gridBackground(page)).toBe('');
});

test('grid mode snaps a dragged node to the grid', async ({ page }) => {
    await page.getByRole('button', { name: /Grid mode/ }).click();
    await expect.poll(() => gridBackground(page)).toContain('linear-gradient');

    //Cose lays the five out with edges about as long as a node is wide, so now and then a neighbor's body covers Alice's
    //center and the press takes that node instead, which then snaps in her place (one whole run in about forty failed
    //so, 2026-11-07; Bob stood over her on purpose does it every time). Alice goes to a spot clear of the others first,
    //inside the view, so the drag's press is hers.
    await page.evaluate(() => {
        const alice = cy.$('#Alice');
        const others = cy.nodes().not(alice);
        const extent = cy.extent();

        for (let y = extent.y1 + 60; y < extent.y2 - 60; y += 30)
            for (let x = extent.x1 + 60; x < extent.x2 - 60; x += 30)
                if (others.every(n => Math.hypot(n.position('x') - x, n.position('y') - y) > 80)) {
                    alice.position({ x, y });

                    return;
                }

        throw new Error('no clear spot for Alice');
    });

    //Grab Alice and drag her toward the canvas center (kept on-canvas). On release the node snaps
    //to the 50-unit grid, so both coordinates land on exact multiples of 50 — a non-snapped float
    //never would, so this also proves the drag actually moved and snapped the node.
    const point = await cyScreenPoint(page, 'Alice');
    const box = await page.locator('#cyGraph').boundingBox();

    await page.mouse.move(point.x, point.y);
    await page.mouse.down();
    await page.mouse.move(box.x + box.width / 2 + 33, box.y + box.height / 2 + 17, { steps: 12 });
    await page.mouse.up();

    await expect.poll(async () => {
        const positions = JSON.parse(await page.evaluate(() => getCytoscapePositions()));
        const alice = positions.find(p => p.id === 'Alice');

        if (alice)
            return alice.x % 50 === 0 && alice.y % 50 === 0;
        else
            return false;
    }).toBe(true);
});

//Grid size (2026-11-06, the user): hovering the grid button opens a slider beneath it, the spacing of the grid's lines
//and of its snapping, in the graph's units.
test('hovering the grid button opens Grid size, which spaces the lines and the snapping', async ({ page }) => {
    const grid = page.getByRole('button', { name: /Grid mode/ });

    await grid.click();
    await expect.poll(() => gridBackground(page)).toContain('linear-gradient');

    await grid.hover();

    const panel = page.getByTestId('grid-size');

    await expect(panel).toBeVisible();
    await expect(panel.locator('label')).toHaveText('Grid size');

    const spacing = () => page.evaluate(() => Math.round(parseFloat(document.getElementById('cyGraph').style.backgroundSize) / cy.zoom()));

    expect(await spacing()).toBe(50);

    await panel.locator('#rngGridSize').fill('100');

    await expect(panel).toContainText('100');
    await expect.poll(spacing).toBe(100);

    //The pointer away, the panel goes.
    await page.mouse.move(5, 5);
    await expect(panel).toHaveCount(0);

    //A node let go of lands on the 100-unit grid.
    const point = await cyScreenPoint(page, 'Alice');
    const box = await page.locator('#cyGraph').boundingBox();

    await page.mouse.move(point.x, point.y);
    await page.mouse.down();
    await page.mouse.move(box.x + box.width / 2 + 33, box.y + box.height / 2 + 17, { steps: 12 });
    await page.mouse.up();

    await expect.poll(async () => {
        const alice = JSON.parse(await page.evaluate(() => getCytoscapePositions())).find(p => p.id === 'Alice');

        return !!alice && alice.x % 100 === 0 && alice.y % 100 === 0;
    }).toBe(true);
});

test('clearing the canvas asks for confirmation — cancel keeps the graph, OK empties it', async ({ page }) => {
    //Dismissing the confirm leaves the graph untouched.
    page.once('dialog', dialog => dialog.dismiss());
    await page.getByRole('button', { name: /Clear results/ }).click();
    expect(await nodeCount(page)).toBe(SAMPLE_NODE_COUNT);

    //Accepting the confirm empties the canvas.
    page.once('dialog', dialog => dialog.accept());
    await page.getByRole('button', { name: /Clear results/ }).click();
    await expect.poll(() => nodeCount(page)).toBe(0);
});
