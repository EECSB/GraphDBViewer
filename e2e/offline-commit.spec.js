//Committing staged edits needs a live database. Offline (drawing mode, no connection) the Commit button
//used to silently no-op — now it behaves exactly like trying to Run a query without a connection: it warns
//in the stats bar and pulses the top-bar connect button to point at where to connect.
//
//Fully offline: the sample graph loads through the Import panel (Visualize DOT), which stages addV/addE
//into the Generated tab without any database, so no server is touched.
const { test, expect } = require('@playwright/test');
const { gotoApp, loadSampleGraph } = require('./helpers');

test('committing offline warns to connect and pulses the connect button', async ({ page }) => {
    await gotoApp(page);
    await loadSampleGraph(page);

    //Visualize stages the drawing's queries and opens the Generated tab — commit them with nothing connected.
    await page.getByRole('button', { name: 'Commit Changes' }).click();

    await expect(page.getByText('No active connection: connect to a database first.')).toBeVisible();

    //The connection button doubles as the mode indicator ("Offline mode" here) and pulses on the warning.
    await expect(page.getByRole('button', { name: /Offline mode/ })).toHaveClass(/gdbv-pulse/);
});
