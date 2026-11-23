const { test, expect } = require('@playwright/test');

test('the robot opens a full portrait trackpad and gestures reach Android', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 780 });
  await page.addInitScript(() => {
    window.mouseEvents = [];
    window.Android = {
      robotNotify: () => {},
      trackpad: (_key, type, dx, dy, delta) => window.mouseEvents.push({ type, dx, dy, delta }),
    };
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, pages: [{
        id: 'pad', title: 'Full-screen trackpad', widgets: [{
          id: 'pad', type: 'trackpad', x: 0, y: 0, w: 12, h: 6,
          options: { control: true, acceleration: false, scrollBar: 'none' },
        }],
      }],
    }));
  });
  await page.goto('/panel/');
  await page.getByRole('button', { name: '触控板' }).click();
  const pad = page.locator('.deck-trackpad');
  await expect(pad).toBeInViewport();
  expect((await pad.boundingBox()).height).toBeGreaterThan(680);

  const client = await page.context().newCDPSession(page);
  const touch = (type, ...points) => client.send('Input.dispatchTouchEvent', {
    type, touchPoints: points.map(([x, y], id) => ({ id, x, y })),
  });
  await touch('touchStart', [110, 180]);
  await touch('touchEnd');
  await touch('touchStart', [110, 180]);
  await touch('touchMove', [170, 200]);
  await touch('touchEnd');
  await touch('touchStart', [110, 180]);
  await expect.poll(() => page.evaluate(() => window.mouseEvents.at(-1)?.type)).toBe('mousedown');
  await touch('touchMove', [175, 220]);
  await touch('touchEnd');
  await touch('touchStart', [100, 180], [150, 180]);
  await touch('touchMove', [100, 230], [150, 230]);
  await touch('touchEnd');
  const events = await page.evaluate(() => window.mouseEvents);
  expect(events.map(e => e.type)).toEqual(['click', 'move', 'mousedown', 'move', 'mouseup', 'scroll']);
  expect(events[1]).toMatchObject({ dx: 60, dy: 20 });
  expect(events[5].delta).toBeLessThan(0);
  await page.evaluate(() => window.onAndroidBack());
  await expect(page.locator('.robot-face')).toBeInViewport();
});
