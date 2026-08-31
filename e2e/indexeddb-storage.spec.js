//The app persists to IndexedDB (via IndexedDbAppStorage) instead of localStorage — a much larger quota,
//with transparent compression. The critical guarantees: existing localStorage users keep their data (a
//one-time migration), and saved state survives a reload (IndexedDB round-trip). This drives the real
//backend in the browser. (The "a failed write warns instead of crashing" path is covered by the bUnit test
//HomeMarkupTests.StorageQuotaExceeded_...; it can't be forced here because Blazor caches the resolved
//gdbvIdb.set reference at boot, so a post-boot override of it has no effect.)
const { test, expect } = require('@playwright/test');
const { gotoApp } = require('./helpers');

test('migrates existing localStorage data into IndexedDB and keeps it across reloads', async ({ page }) => {
    //Seed the OLD backend before the app boots, as an upgrading user would have it.
    await page.addInitScript(() => {
        //Only seed once — addInitScript runs on every navigation, and we must not re-seed after migration.
        if (!localStorage.getItem('__seeded')) {
            localStorage.setItem('graphdbviewer:darkMode', 'false');//light theme, not the dark default
            localStorage.setItem('graphdbviewer:queryHistory', '["g.V().limit(7)"]');
            localStorage.setItem('__seeded', '1');
        }
    });

    await gotoApp(page);

    //The migrated dark-mode=false was read from IndexedDB and applied — the app is in light theme, not the
    //dark default. That proves migration ran AND the app now reads from IndexedDB.
    await expect.poll(async () => {
        return await page.evaluate(() => document.documentElement.getAttribute('data-bs-theme'));
    }).toBe('light');

    //The heavier value (query history) landed in IndexedDB too, and the one-time flag is set.
    const migrated = await page.evaluate(async () => {
        return {
            history: await window.gdbvIdb.get('graphdbviewer:queryHistory'),
            flag: await window.gdbvIdb.get('__gdbv_migrated')
        };
    });
    expect(migrated.history).toContain('g.V().limit(7)');
    expect(migrated.flag).toBe('1');

    //Reload: the data must survive (IndexedDB persists), and the second boot must NOT re-migrate (flag guards it).
    await page.reload();
    await expect(page.getByRole('button', { name: /Import \/ Export/ })).toBeVisible({ timeout: 60000 });
    await expect.poll(async () => {
        return await page.evaluate(() => document.documentElement.getAttribute('data-bs-theme'));
    }).toBe('light');
});
