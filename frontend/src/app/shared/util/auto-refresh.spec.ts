import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { autoRefresh } from './auto-refresh';

describe('autoRefresh', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  function host(options: { everyMs: number; when?: () => boolean }) {
    const refresh = vi.fn();
    @Component({ template: '' })
    class Host {
      constructor() {
        autoRefresh(refresh, options);
      }
    }
    return { fixture: TestBed.createComponent(Host), refresh };
  }

  it('refreshes on every tick while allowed, and stops with its owner', () => {
    let allowed = true;
    const { fixture, refresh } = host({ everyMs: 1000, when: () => allowed });

    vi.advanceTimersByTime(2000);
    allowed = false;
    vi.advanceTimersByTime(1000);
    fixture.destroy();
    allowed = true;
    vi.advanceTimersByTime(5000);

    expect(refresh).toHaveBeenCalledTimes(2);
  });

  it('does nothing when the interval is 0', () => {
    const { refresh } = host({ everyMs: 0 });

    vi.advanceTimersByTime(60_000);

    expect(refresh).not.toHaveBeenCalled();
  });
});
