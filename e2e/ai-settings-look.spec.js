//How the AI settings window is drawn (2026-11-02, the user): a rounded window with its border all round, the model form
//inside it a plain card, and its scrolling body ending above the resize grip rather than under it.
const { test, expect } = require('@playwright/test');
const { gotoApp, openAccountMenu } = require('./helpers');

async function openAiSettings(page) {
    await gotoApp(page);
    await openAccountMenu(page);
    await page.getByRole('button', { name: 'AI settings', exact: true }).click();

    const window = page.getByRole('dialog', { name: 'AI settings' });

    await expect(window).toBeVisible();

    return window;
}

//The bar's own rule, square with a bottom border only, had caught the window too, since the bar opens it beside itself.
test('the AI settings window is rounded, with its border all round', async ({ page }) => {
    const window = await openAiSettings(page);

    const look = await window.evaluate(w => {
        const style = getComputedStyle(w);

        return {
            radius: parseFloat(style.borderBottomLeftRadius),
            widths: [style.borderTopWidth, style.borderRightWidth, style.borderBottomWidth, style.borderLeftWidth].map(parseFloat)
        };
    });

    expect(look.radius).toBeGreaterThan(0);

    for (const width of look.widths)
        expect(width).toBeGreaterThan(0);
});

//It had the secondary color's border, and the shadow of the bar's menus.
test('the AI model form is a plain card: the line color\'s border and no shadow', async ({ page }) => {
    const window = await openAiSettings(page);

    await window.getByTestId('llm-picker-chevron').click();
    await window.getByTitle('Add an AI model').click();

    const form = window.getByTestId('llm-form');

    await expect(form).toBeVisible();

    const look = await form.evaluate(card => ({
        border: getComputedStyle(card).borderTopColor,
        line: getComputedStyle(card.closest('.gdbv-dialog').querySelector('.gdbv-dialog-header')).borderBottomColor,
        shadow: getComputedStyle(card).boxShadow
    }));

    expect(look.border).toBe(look.line);
    expect(look.shadow).toBe('none');
});

//The grip sat in the window's corner on top of the scrollbar's end.
test('the AI settings body ends above the resize grip', async ({ page }) => {
    const window = await openAiSettings(page);

    const body = await window.locator('.card-body').boundingBox();
    const grip = await window.locator('.gdbv-dialog-resize').boundingBox();

    expect(grip.y).toBeGreaterThanOrEqual(body.y + body.height - 0.5);
});
