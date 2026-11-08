//The query panel beside the graph (2026-11-03, the user): its tabs over its card, a switch of tab that moves nothing,
//and the new component at the card's foot. Only a browser lays these out.
const { test, expect } = require('@playwright/test');
const { cyScreenPoint, gotoApp, loadSampleGraph } = require('./helpers');

//The panel's tabs as they show: one copy of the two the page draws is hidden at any width.
function tabs(page) {
    return page.locator('[data-tour="query-tabs"]:visible');
}

//A fresh account opens on the empty canvas; offline mode puts the workspace, and the panel, on the page.
async function openOffline(page) {
    await gotoApp(page);
    await page.getByRole('button', { name: /Offline mode/ }).click();
}

//The handle under the panel sizes the editor, sticks at its default size, and is remembered (2026-11-05, the user).
test('the query panel\'s editor is sized by its handle and sticks at its default size', async ({ page }) => {
    await openOffline(page);

    const pane = page.locator('.gdbv-editor-pane');
    const editor = pane.locator('.monaco-editor').first();
    const handle = page.getByTestId('editor-resize');

    await expect(editor).toBeVisible();

    const height = async (where) => (await where.boundingBox()).height;
    const drag = async (by, end) => {
        const box = await handle.boundingBox();
        const x = box.x + box.width / 2;
        const y = box.y + box.height / 2;

        await page.mouse.move(x, y);
        await page.mouse.down();
        await expect(handle).toHaveClass(/is-dragging/);
        await page.mouse.move(x, y + by, { steps: 5 });

        if (end)
            await page.mouse.up();
    };

    const startPane = await height(pane);
    const startEditor = await height(editor);

    //Smaller, as there is always room for that; the editor follows the box.
    await drag(-80, true);
    await expect.poll(() => height(pane)).toBeCloseTo(startPane - 80, -1);
    await expect.poll(() => height(editor)).toBeCloseTo(startEditor - 80, -1);

    //Within reach of the default, it sticks there, and the grip says so during the drag.
    await drag(70, false);
    await expect(handle).toHaveClass(/is-default/);
    await expect.poll(() => height(pane)).toBeCloseTo(startPane, 0);
    await page.mouse.up();
    await expect(handle).not.toHaveClass(/is-default/);

    //Double-click goes back from anywhere.
    await drag(-60, true);
    await expect.poll(() => height(pane)).toBeCloseTo(startPane - 60, -1);
    await handle.dblclick();
    await expect.poll(() => height(pane)).toBeCloseTo(startPane, 0);

    //Remembered over a reload.
    await drag(-60, true);
    await expect.poll(() => height(pane)).toBeCloseTo(startPane - 60, -1);
    await page.reload();
    expect(await page.evaluate(() => parseFloat(document.documentElement.style.getPropertyValue('--gdbv-editor-extra')))).toBeCloseTo(-60, -1);
});

//Each tab's toolbar and content fill one box of one height, so whatever is under it stays where it is.
test('a switch of the query panel\'s tab moves nothing under it', async ({ page }) => {
    await openOffline(page);
    await expect(tabs(page)).toHaveCount(1);

    const bottoms = new Set();

    for (const name of ['Query', 'Generated', 'Examples', 'History']) {
        await tabs(page).locator('.nav-link', { hasText: name }).click();
        await expect(tabs(page).locator('.nav-link.active')).toContainText(name);
        bottoms.add(await page.locator('.gdbv-editor-pane').evaluate(pane => Math.round(pane.getBoundingClientRect().bottom)));
    }

    expect([...bottoms]).toHaveLength(1);
});

//Side by side, the tabs stand on the panel's card from above, as the query tabs do on the graph's, and the card
//keeps no copy of its own.
test('side by side, the query panel\'s tabs stand on its card from the row above', async ({ page }) => {
    await openOffline(page);
    await expect(tabs(page)).toHaveCount(1);

    const tabsBottom = await tabs(page).evaluate(list => list.getBoundingClientRect().bottom);
    const cardTop = await page.locator('.split-side > .card').evaluate(card => card.getBoundingClientRect().top);

    expect(Math.abs(tabsBottom - cardTop)).toBeLessThan(2);
    await expect(page.locator('.split-side > .card [data-tour="query-tabs"]')).toBeHidden();
});

//The selected element's card runs down to the new component, however little it holds, rather than stopping at its
//content and leaving the space under it empty (2026-11-03, the user). A tall window, so what Alice holds leaves room.
test('the selected element\'s card reaches down to the new component', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 1600 });
    await gotoApp(page);
    await loadSampleGraph(page);

    const point = await cyScreenPoint(page, 'Alice');

    await page.mouse.click(point.x, point.y);
    await expect(page.getByTestId('add-property')).toBeVisible();

    const room = await page.getByTestId('element-card').evaluate(card => {
        const rule = document.querySelector('[data-testid=new-component] hr').getBoundingClientRect();

        return rule.top - card.getBoundingClientRect().bottom;
    });

    expect(room).toBeLessThan(20);
});

//The line over the selected element's card has as much room under it as over it on every tab of the editor, and the
//editor's handle is centered on it (2026-11-05, the user).
test('the line over the selected element\'s card has as much room under it as over it, and the handle on it', async ({ page }) => {
    await gotoApp(page);
    await loadSampleGraph(page);

    const point = await cyScreenPoint(page, 'Alice');

    await page.mouse.click(point.x, point.y);
    await expect(page.getByTestId('add-property')).toBeVisible();

    //Query's toolbar wraps at some widths, so Generated stands for the two with room for help text under the editor.
    for (const name of ['Generated', 'Examples', 'History']) {
        await tabs(page).locator('.nav-link', { hasText: name }).click();
        await expect(tabs(page).locator('.nav-link.active')).toHaveText(new RegExp(name));

        const m = await page.getByTestId('element-rule').evaluate(rule => {
            const box = el => el.getBoundingClientRect();
            const card = box(document.querySelector('[data-testid=element-card]'));
            const pane = document.querySelector('.gdbv-editor-pane');
            const grip = box(document.querySelector('.gdbv-editor-resize-grip'));

            return {
                over: box(rule).top - box(pane.lastElementChild).bottom,
                under: card.top - box(rule).bottom,
                off: (grip.top + grip.bottom) / 2 - (box(rule).top + box(rule).bottom) / 2
            };
        });

        expect(Math.abs(m.under - m.over), name).toBeLessThan(2);
        expect(Math.abs(m.off), name).toBeLessThan(1);
    }
});

//On Generated, Auto-reload stands on the row's foot with Commit Changes rather than in its middle (2026-11-04, the user).
test('Auto-reload stands on the foot of Commit Changes\' row', async ({ page }) => {
    await openOffline(page);
    await tabs(page).locator('.nav-link', { hasText: 'Generated' }).click();

    //Read at once, and again until the panel has settled: it renders anew as offline mode comes in.
    await expect(async () => {
        const m = await page.getByTestId('auto-reload').evaluate(check => {
            const commit = [...check.parentElement.querySelectorAll('button')].find(b => b.textContent.includes('Commit Changes'));
            const foot = commit.getBoundingClientRect().bottom;
            const input = check.querySelector('input').getBoundingClientRect();
            const label = check.querySelector('label');
            const line = label.getBoundingClientRect();

            return {
                input: input.bottom - foot,
                label: line.bottom - foot,
                lineToFont: line.height / parseFloat(getComputedStyle(label).fontSize),
                indent: input.left - check.getBoundingClientRect().left
            };
        });

        //Box and label down on the button's foot, the label's line no taller than its text, so its bottom is the
        //text's, and the box at the row's start rather than indented or cut off.
        expect(Math.abs(m.input)).toBeLessThan(1.5);
        expect(Math.abs(m.label)).toBeLessThan(1.5);
        expect(m.lineToFont).toBeLessThan(1.2);
        expect(Math.abs(m.indent)).toBeLessThan(1);
    }).toPass({ timeout: 10000 });
});

//Create Component is as tall as the label's box beside it, top and bottom in line (2026-11-04, the user; it stood 3px
//taller).
test('Create Component is as tall as its input', async ({ page }) => {
    await gotoApp(page);
    await loadSampleGraph(page);

    const block = page.getByTestId('new-component');
    const input = await block.locator('input').boundingBox();
    const button = await block.locator('button').boundingBox();

    expect(Math.abs(button.height - input.height)).toBeLessThan(1);
    expect(Math.abs(button.y - input.y)).toBeLessThan(1);
});

//The new component waits at the card's foot, however little is above it, and keeps room over its line when the
//selected element's card fills the space between.
test('the new component stands at the foot of the query panel\'s card', async ({ page }) => {
    await gotoApp(page);
    await loadSampleGraph(page);

    const block = page.getByTestId('new-component');

    await expect(block).toBeVisible();

    const gap = () => block.evaluate(el => {
        const card = el.closest('.card').getBoundingClientRect();
        const box = el.getBoundingClientRect();
        const rule = el.querySelector('hr').getBoundingClientRect();
        const above = el.previousElementSibling.getBoundingClientRect();

        return { foot: card.bottom - box.bottom, room: rule.top - above.bottom };
    });

    expect((await gap()).foot).toBeLessThan(12);

    const point = await cyScreenPoint(page, 'Alice');

    await page.mouse.click(point.x, point.y);
    await expect(page.getByTestId('add-property')).toBeVisible();

    const selected = await gap();

    expect(selected.foot).toBeLessThan(12);
    expect(selected.room).toBeGreaterThanOrEqual(15);
});
