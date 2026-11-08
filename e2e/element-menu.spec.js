//A right-click on a graph item, in 2D and in 3D, opens the page's menu for it at the pointer: Explore and Delete
//(2026-11-04, the user). Only a browser fires the right-click, so the canvases' hooks are proved here.
const { test, expect } = require('@playwright/test');
const { cyScreenPoint, gotoApp, loadSampleGraph, switchTo3d } = require('./helpers');

//Whether the canvas holds back the browser's own menu: a cancelable contextmenu it is sent comes back prevented.
function browserMenuHeldBack(page, selector) {
    return page.locator(selector).first().evaluate(el => {
        const e = new MouseEvent('contextmenu', { bubbles: true, cancelable: true });

        el.dispatchEvent(e);

        return e.defaultPrevented;
    });
}

test('a right-click on the empty 2D canvas offers a new component there and clearing the canvas', async ({ page }) => {
    await gotoApp(page);
    await loadSampleGraph(page);

    const box = await page.locator('#cyGraph').boundingBox();
    const empty = { x: box.x + 12, y: box.y + box.height - 12 };
    const nodes = () => page.evaluate(() => window.cyInstance ? window.cyInstance.nodes().length : -1);

    await page.mouse.click(empty.x, empty.y, { button: 'right' });

    const menu = page.getByTestId('canvas-menu');

    await expect(menu).toBeVisible();
    await expect(menu).toContainText('New component');
    await expect(menu).toContainText('Clear canvas');
    await expect(page.getByTestId('element-menu')).toHaveCount(0);

    const place = await menu.boundingBox();

    expect(Math.abs(place.x - empty.x)).toBeLessThan(2);
    expect(Math.abs(place.y - empty.y)).toBeLessThan(2);

    await page.getByTestId('canvas-menu-new').click();
    await expect(menu).toHaveCount(0);
    await expect(page.locator('[data-tour="query-tabs"]:visible .nav-link', { hasText: 'Generated' })).toBeVisible();

    await page.mouse.click(empty.x, empty.y, { button: 'right' });
    page.once('dialog', dialog => dialog.accept());
    await page.getByTestId('canvas-menu-clear').click();
    await expect(menu).toHaveCount(0);
});

test('a right-click on a 2D vertex opens its menu at the pointer, and Delete stages it', async ({ page }) => {
    await gotoApp(page);
    await loadSampleGraph(page);

    const point = await cyScreenPoint(page, 'Alice');

    await page.mouse.click(point.x, point.y, { button: 'right' });

    const menu = page.getByTestId('element-menu');

    await expect(menu).toBeVisible();
    await expect(menu).toContainText('Alice');

    const box = await menu.boundingBox();

    expect(Math.abs(box.x - point.x)).toBeLessThan(2);
    expect(Math.abs(box.y - point.y)).toBeLessThan(2);
    expect(await browserMenuHeldBack(page, '#cyGraph')).toBe(true);

    //As narrow as its widest row, no wider, and every row's icon starting where the head's text does (2026-11-06, the
    //user, twice: it was wide and half empty, and the rows stood in from the head).
    const fit = await menu.evaluate(m => {
        const left = m.getBoundingClientRect().left;
        const head = m.querySelector('.gdbv-element-menu-head');
        const range = document.createRange();

        range.selectNodeContents(head);

        const rows = [...m.querySelectorAll('.gdbv-menu-row')];
        const widest = Math.max(...rows.map(r => [...r.childNodes].reduce((w, c) => {
            const k = document.createRange();

            k.selectNodeContents(c);

            return Math.max(w, k.getBoundingClientRect().right - r.getBoundingClientRect().left);
        }, 0)));

        return {
            width: m.getBoundingClientRect().width,
            widest,
            headIn: range.getBoundingClientRect().left - left,
            iconsIn: rows.map(r => r.querySelector('.gdbv-menu-row-icon').getBoundingClientRect().left - left)
        };
    });

    expect(fit.width - fit.widest).toBeLessThan(16);
    fit.iconsIn.forEach(x => expect(Math.abs(x - fit.headIn)).toBeLessThan(2));
    expect(fit.headIn).toBeLessThan(10);

    await page.getByTestId('element-menu-delete').click();

    await expect(menu).toHaveCount(0);
    await expect(page.locator('[data-tour="query-tabs"]:visible .nav-link.active')).toContainText('Generated');
});

test('a right-click on a 3D vertex opens its menu', async ({ page }) => {
    await gotoApp(page);
    await loadSampleGraph(page);
    await switchTo3d(page, 5);

    //Where Alice is drawn, read again and right-clicked again until the menu opens: the layout and the camera may still
    //be moving her when she is first found.
    await expect(async () => {
        const point = await page.evaluate(() => {
            const g = window.graph3DInterop.instance;
            const n = g.graphData().nodes.find(x => x.id === 'Alice');
            const c = g.graph2ScreenCoords(n.x || 0, n.y || 0, n.z || 0);
            const r = g.renderer().domElement.getBoundingClientRect();

            return { x: Math.round(r.left + c.x), y: Math.round(r.top + c.y) };
        });

        await page.mouse.click(point.x, point.y, { button: 'right' });
        await expect(page.getByTestId('element-menu')).toContainText('Alice', { timeout: 1000 });
    }).toPass({ timeout: 30000 });
    await expect(page.getByTestId('element-menu-expand')).toBeVisible();
    expect(await browserMenuHeldBack(page, '#graph3d')).toBe(true);
});

//Style on the menu opens the Style dialog on what was right-clicked, as its card's palette does (2026-11-04, the user):
//the canvas hands the menu what a click would, and it is selected first, its card showing.
test('Style on a 2D vertex\'s menu selects it and opens Style on its label', async ({ page }) => {
    await gotoApp(page);
    await loadSampleGraph(page);

    const point = await cyScreenPoint(page, 'Alice');

    await page.mouse.click(point.x, point.y, { button: 'right' });
    await page.getByTestId('element-menu-style').click();

    await expect(page.getByTestId('element-menu')).toHaveCount(0);
    await expect(page.locator('.gdbv-style-window .gdbv-dialog-header')).toContainText('Style');
    await expect(page.getByRole('button', { name: /^All person nodes/ })).toBeVisible();
    await expect(page.getByTestId('element-editors')).toBeAttached();
});
