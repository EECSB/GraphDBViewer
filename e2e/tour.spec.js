//The guided tour (GuidedTour.razor, 2026-11-03): GDS Viewer's walkthrough brought across. Started from the settings,
//it walks the viewer's parts one at a time, a hole in a shade over each and a card beside it, and shows only the parts
//that are on the page.
const { test, expect } = require('@playwright/test');
const { gotoApp, loadSampleGraph, openAccountMenu, waitForStableLayout } = require('./helpers');

//Every stop's title with a graph on the canvas, in order: all but "Where to start", which is the empty canvas's, and Ask
//AI, whose button is the query editor's, out only on the Query tab. More connections and Share are the web edition's,
//whose bar has them; Treeality's has neither, and the tour leaves them out there.
async function loadedSteps(page) {
    const web = await page.getByTestId('viewer-add-connection').count() > 0;
    const steps = ['The connection'];

    if (web)
        steps.push('More connections');

    steps.push('Import and export', 'Four views', 'The graph', 'Editing', 'Finding your way', 'Queries');

    if (web)
        steps.push('Share');

    steps.push('Settings', 'That is the tour');

    return steps;
}

//From the settings: the account's menu on a host with accounts, the Settings button on one without. Treeality draws a
//Settings button for a moment as it opens, until it knows the account, so this tries until the row is there.
async function startTour(page) {
    await expect(async () => {
        if (await page.getByTestId('shell-account').count() > 0)
            await openAccountMenu(page);
        else
            await page.getByTestId('shell-settings').click({ timeout: 2000 });

        await expect(page.getByTestId('shell-tour')).toBeVisible({ timeout: 2000 });
    }).toPass();

    await page.getByTestId('shell-tour').click();
    await expect(page.getByTestId('tour-card')).toBeVisible();
}

//The hole the shade's clip-path leaves, as the rectangle of its last five points.
async function hole(page) {
    return await page.getByTestId('tour-shade').evaluate(shade => {
        //The browser writes the window's corners back with px too, so the hole is the last ten.
        const numbers = shade.style.clipPath.match(/-?[\d.]+px/g).map(n => parseFloat(n)).slice(-10);

        return { left: numbers[0], top: numbers[1], right: numbers[2], bottom: numbers[5] };
    });
}

//Every title from here to the end, by the right arrow.
async function walk(page) {
    const titles = [];
    const title = page.getByTestId('tour-title');

    for (;;) {
        titles.push(await title.textContent());

        if (await page.getByTestId('tour-next').getAttribute('aria-label') === 'Done')
            return titles;

        const before = titles[titles.length - 1];

        await page.keyboard.press('ArrowRight');
        await expect(title).not.toHaveText(before);
    }
}

test.describe('with a graph', () => {
    test.beforeEach(async ({ page }) => {
        await gotoApp(page);
        await loadSampleGraph(page);
    });

    test('the tour walks every part on the page, in order, and counts them', async ({ page }) => {
        //On the Query tab its editor is out, and with it the Ask AI button.
        await page.getByRole('button', { name: 'Query', exact: true }).click();

        const steps = await loadedSteps(page);

        steps.splice(steps.indexOf('Queries') + 1, 0, 'AI');

        await startTour(page);
        await expect(page.getByTestId('tour-count')).toHaveText(`1 of ${steps.length + 1}`);

        const titles = await walk(page);

        expect(titles[0]).toMatch(/^Welcome to /);
        expect(titles.slice(1)).toEqual(steps);
        await expect(page.getByTestId('tour-count')).toHaveText(`${steps.length + 1} of ${steps.length + 1}`);
    });

    //The query panel's tabs are drawn twice, over its card and in it, one hidden at any width (2026-11-03): in a window
    //too narrow for the two columns the one in the card shows, and the tour stops there too.
    test('in a narrow window the tour still stops at the query panel\'s tabs, in its card', async ({ page }) => {
        await page.setViewportSize({ width: 900, height: 900 });
        await startTour(page);

        expect(await walk(page)).toContain('Queries');
    });

    test('the hole is round the part, and the card beside it', async ({ page }) => {
        const steps = await loadedSteps(page);

        await startTour(page);

        for (let i = 0; i <= steps.indexOf('Four views'); i++)
            await page.keyboard.press('ArrowRight');

        await expect(page.getByTestId('tour-title')).toHaveText('Four views');

        //The shade slides from step to step, so its hole is read once it has stopped.
        await expect.poll(async () => {
            const cut = await hole(page);
            const part = await page.locator('[aria-label="Visualization mode"]').boundingBox();

            return Math.max(Math.abs(cut.left - (part.x - 4)), Math.abs(cut.top - (part.y - 4)), Math.abs(cut.right - (part.x + part.width + 4)), Math.abs(cut.bottom - (part.y + part.height + 4)));
        }).toBeLessThan(1);

        const part = await page.locator('[aria-label="Visualization mode"]').boundingBox();
        const card = await page.getByTestId('tour-card').boundingBox();

        expect(card.y).toBeGreaterThanOrEqual(part.y + part.height);
    });

    test('Escape, the cross and Done each end it, and it starts again from the first stop', async ({ page }) => {
        await startTour(page);

        const count = await page.getByTestId('tour-count').textContent();

        await page.keyboard.press('ArrowRight');
        await page.keyboard.press('Escape');
        await expect(page.getByTestId('tour-card')).toHaveCount(0);

        //Started again: at the first stop, with the page's parts asked again rather than every stop there is.
        await startTour(page);
        await expect(page.getByTestId('tour-count')).toHaveText(count);
        await page.getByTestId('tour-close').click();
        await expect(page.getByTestId('tour-card')).toHaveCount(0);

        await startTour(page);
        await walk(page);

        //The right arrow stops at the last stop: ending is Done's to say.
        await page.keyboard.press('ArrowRight');
        await expect(page.getByTestId('tour-title')).toHaveText('That is the tour');

        await page.getByTestId('tour-next').click();
        await expect(page.getByTestId('tour-card')).toHaveCount(0);
        await expect(page.getByTestId('tour-shade')).toHaveCount(0);
    });

    test('Back goes back, and is off on the first stop', async ({ page }) => {
        await startTour(page);
        await expect(page.getByTestId('tour-back')).toBeDisabled();

        await page.keyboard.press('ArrowLeft');
        await expect(page.getByTestId('tour-title')).toHaveText(/^Welcome to /);
        await expect(page.getByTestId('tour-count')).toHaveText(/^1 of/);

        await page.getByTestId('tour-next').click();
        await expect(page.getByTestId('tour-title')).toHaveText('The connection');

        await page.getByTestId('tour-back').click();
        await expect(page.getByTestId('tour-title')).toHaveText(/^Welcome to /);
    });

    test('the page hears no key while the tour is open: Delete leaves the chosen node', async ({ page }) => {
        await page.getByLabel('Preview data changes').check();
        await waitForStableLayout(page);
        await page.evaluate(() => {
            cy.elements().unselect();
            cy.getElementById('Carol').select();
        });

        await startTour(page);
        await page.keyboard.press('Delete');
        await page.keyboard.press('Escape');

        await expect(page.getByTestId('tour-card')).toHaveCount(0);
        expect(await page.evaluate(() => cy.getElementById('Carol').length)).toBe(1);

        //Ended, the tour lets the keys go, and the same Delete now deletes it.
        await page.keyboard.press('Delete');
        await expect.poll(() => page.evaluate(() => cy.getElementById('Carol').length)).toBe(0);
    });

    //A window narrowed, a panel closed: a part gone since the walk started is passed over, in the way the walk was going.
    test('a part gone since the walk started is passed over', async ({ page }) => {
        const steps = await loadedSteps(page);

        await startTour(page);
        await page.locator('[aria-label="Visualization mode"]').evaluate(group => group.style.display = 'none');

        for (let i = 0; i < steps.indexOf('Four views'); i++)
            await page.keyboard.press('ArrowRight');

        await expect(page.getByTestId('tour-title')).toHaveText('Import and export');

        await page.keyboard.press('ArrowRight');
        await expect(page.getByTestId('tour-title')).toHaveText('The graph');

        await page.keyboard.press('ArrowLeft');
        await expect(page.getByTestId('tour-title')).toHaveText('Import and export');
    });

    test('Tab stays on the card', async ({ page }) => {
        await startTour(page);
        await page.getByTestId('tour-next').click();

        for (let i = 0; i < 4; i++) {
            await page.keyboard.press('Tab');
            expect(await page.evaluate(() => !!document.activeElement.closest('[data-testid="tour-card"]'))).toBe(true);
        }
    });
});

test('on an empty canvas the tour shows where to start, and leaves out the graph\'s parts', async ({ page }) => {
    await gotoApp(page);
    await startTour(page);

    const titles = await walk(page);

    expect(titles).toContain('Where to start');
    expect(titles).not.toContain('Editing');
    expect(titles).not.toContain('The graph');
    await expect(page.getByTestId('tour-count')).toHaveText(`${titles.length} of ${titles.length}`);
});
