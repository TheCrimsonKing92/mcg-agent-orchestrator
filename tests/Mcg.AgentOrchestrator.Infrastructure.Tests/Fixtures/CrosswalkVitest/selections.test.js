import { expect, test } from 'vitest';

test('crosswalk passing selection', () => {
  expect(2 + 2).toBe(4);
});

test('crosswalk known failing selection', () => {
  expect('actual').toBe('intentionally different');
});
