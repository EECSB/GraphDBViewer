//2D (Cytoscape) rendering and canvas interaction: a DOT graph imported offline renders on the
//canvas, clicking a node selects it into the property panel, clicking empty space deselects.
const { test, expect } = require('@playwright/test');
const { gotoApp, loadSampleGraph, cyPositions, cyScreenPoint, SAMPLE_NODE_COUNT } = require('./helpers');

test.beforeEach(async ({ page }) => {
    await gotoApp(page);
    await loadSampleGraph(page);
});

test('renders the imported DOT graph on the 2D canvas', async ({ page }) => {
    await expect(page.locator('#cyGraph canvas').first()).toBeVisible();

    const positions = await cyPositions(page);
    expect(positions).toHaveLength(SAMPLE_NODE_COUNT);
    expect(positions.map(p => p.id).sort()).toEqual(['Acme', 'Alice', 'Bob', 'Carol', 'Globex']);

    //Edges made it into the graph too (id format: source->target:label).
    const edge = await page.evaluate(() => getCytoscapeElementInfo('Alice->Bob:knows'));
    expect(edge).not.toBeNull();
    expect(edge.data.source).toBe('Alice');
    expect(edge.data.target).toBe('Bob');
});

test('clicking a node on the canvas selects it into the property panel', async ({ page }) => {
    const point = await cyScreenPoint(page, 'Alice');
    await page.mouse.click(point.x, point.y);

    const panel = page.locator('.card', { hasText: 'Component Properties' }).last();
    await expect(panel).toBeVisible();
    await expect(panel.getByText('node', { exact: true })).toBeVisible();
    await expect(panel.getByText('Alice')).toBeVisible();
});

test('clicking empty canvas space deselects the element', async ({ page }) => {
    const point = await cyScreenPoint(page, 'Bob');
    await page.mouse.click(point.x, point.y);
    await expect(page.getByText('Component Properties')).toBeVisible();

    //The layout is fitted with 50px padding, so the container's top-left corner is empty.
    const box = await page.locator('#cyGraph').boundingBox();
    await page.mouse.click(box.x + 8, box.y + 8);
    await expect(page.getByText('Component Properties')).toBeHidden();
});

//Middle-drag pans from anywhere, including from on top of a node. Cytoscape's own left-drag pan only
//works from empty background, so on a dense graph you had to find a gap before you could move the view.
//Starting the drag on a node is the whole point of the test: the view must move and the node must not.
test('middle-button drag pans the canvas, even when it starts on a node', async ({ page }) => {
    const before = await cyPositions(page);
    const start = await cyScreenPoint(page, 'Alice');

    await page.mouse.move(start.x, start.y);
    await page.mouse.down({ button: 'middle' });
    await page.mouse.move(start.x + 120, start.y + 80, { steps: 10 });
    await page.mouse.up({ button: 'middle' });

    const moved = await cyScreenPoint(page, 'Alice');

    //The node is where the drag left it on screen: the viewport moved under it.
    expect(Math.abs((moved.x - start.x) - 120)).toBeLessThan(12);
    expect(Math.abs((moved.y - start.y) - 80)).toBeLessThan(12);

    //And it is still where it was in the graph — panning is not dragging.
    const after = await cyPositions(page);
    const find = (list, id) => list.find(p => p.id === id);

    expect(find(after, 'Alice').x).toBeCloseTo(find(before, 'Alice').x, 1);
    expect(find(after, 'Alice').y).toBeCloseTo(find(before, 'Alice').y, 1);
});
