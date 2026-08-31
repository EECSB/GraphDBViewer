//The AI popups are windows, not modals. Four things follow from that and none of them are visible in
//the markup alone, so they are asserted against a real browser: nothing behind them is dimmed or
//blocked, the title bar drags them, the corner grip resizes them, and closing one hides it rather than
//destroying it, so a half-written prompt is still there when it is opened again.
const { test, expect } = require('@playwright/test');
const { gotoApp } = require('./helpers');

const panel = '.gdbv-dialog:not(.d-none)';

async function box(page) {
    return await page.locator(panel).evaluate(el => {
        const r = el.getBoundingClientRect();
        return { left: Math.round(r.left), top: Math.round(r.top), w: Math.round(r.width), h: Math.round(r.height) };
    });
}

test('an AI popup is a window: undimmed, draggable, resizable, and it keeps what you typed', async ({ page }) => {
    await gotoApp(page);

    await page.getByRole('button', { name: /Generate with AI/ }).click();
    await expect(page.locator(panel)).toBeVisible();

    //Nothing behind it is dimmed, and nothing behind it is blocked: the button that opened it is still
    //there to be pressed. A Bootstrap modal would have covered the page with a backdrop.
    expect(await page.evaluate(() => document.querySelectorAll('.modal-backdrop, .modal.show').length)).toBe(0);
    await expect(page.getByRole('button', { name: /Generate with AI/ })).toBeEnabled();

    //Resizing is our own grip, not the browser's: drag it and the window follows, which is the whole
    //reason for drawing one — the native resizer is a few pixels with a hit area to match.
    const before = await box(page);
    const gripBox = await page.locator(panel + ' .gdbv-dialog-resize').boundingBox();

    await page.mouse.move(gripBox.x + gripBox.width / 2, gripBox.y + gripBox.height / 2);
    await page.mouse.down();
    await page.mouse.move(gripBox.x + gripBox.width / 2 + 120, gripBox.y + gripBox.height / 2 + 60, { steps: 10 });
    await page.mouse.up();

    const resized = await box(page);
    expect(resized.w - before.w).toBeGreaterThan(90);
    expect(resized.h - before.h).toBeGreaterThan(40);

    //Pin it to an exact size so what is reopened and reloaded below can be checked against a number.
    await page.locator(panel).evaluate(el => { el.style.width = '640px'; el.style.height = '520px'; });
    await page.waitForTimeout(500);//the geometry save is debounced

    const bar = await page.locator(panel + ' .gdbv-dialog-header').boundingBox();
    const grabX = bar.x + bar.width / 2 - 60;//left of center, clear of the close button
    const grabY = bar.y + bar.height / 2;

    await page.mouse.move(grabX, grabY);
    await page.mouse.down();
    await page.mouse.move(grabX + 90, grabY + 70, { steps: 10 });
    await page.mouse.up();

    const moved = await box(page);
    expect(moved.left - resized.left).toBeGreaterThan(60);
    expect(moved.top - resized.top).toBeGreaterThan(50);

    await page.locator(panel + ' textarea').first().fill('find every product made of metal');
    await page.locator(panel + ' .btn-close').click();
    await expect(page.locator(panel)).toHaveCount(0);

    await page.getByRole('button', { name: /Generate with AI/ }).click();
    await expect(page.locator(panel)).toBeVisible();

    //Hidden, not unmounted: the text is still there because the component never lost its state.
    expect(await page.locator(panel + ' textarea').first().inputValue()).toBe('find every product made of metal');

    const reopened = await box(page);
    expect(Math.abs(reopened.left - moved.left)).toBeLessThan(4);
    expect(Math.abs(reopened.top - moved.top)).toBeLessThan(4);
    expect(reopened.w).toBe(640);

    //Geometry is stored rather than merely held, so it outlives the page.
    await page.reload();
    await page.getByRole('button', { name: /Generate with AI/ }).click();
    await expect(page.locator(panel)).toBeVisible();

    const afterReload = await box(page);
    expect(afterReload.w).toBe(640);
    expect(Math.abs(afterReload.left - moved.left)).toBeLessThan(4);
});

//Closing a window used to shrink it. The ResizeObserver that watches for a zoom or a rotated phone also
//fires when the panel is hidden, where it measures nothing — so closing one recorded nothing-by-nothing
//as its size, and reopening it restored that, clamped up to the stylesheet's minimum. It looked like the
//defaults were wrong; it was the last close.
test('a closed window reopens at the size it was, not at the minimum', async ({ page }) => {
    await gotoApp(page);
    await page.getByRole('button', { name: /Offline mode/ }).click();

    const open = async () => {
        await page.getByRole('button', { name: /Ask AI/ }).click();
        await expect(page.locator(panel)).toBeVisible();

        const b = await page.locator(panel).boundingBox();

        return { w: Math.round(b.width), h: Math.round(b.height) };
    };

    const first = await open();

    await page.locator(panel + ' .btn-close').click();
    await expect(page.locator(panel)).toHaveCount(0);
    await page.waitForTimeout(600);//the geometry save is debounced by 250ms

    const second = await open();

    expect(second.w).toBe(first.w);
    expect(second.h).toBe(first.h);
});
