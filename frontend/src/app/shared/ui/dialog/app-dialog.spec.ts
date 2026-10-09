import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AppDialog } from './app-dialog';

@Component({ template: '<h2 id="test-title">Title</h2>' })
class TestDialog {}

describe('AppDialog', () => {
  afterEach(() =>
    document.querySelectorAll('.cdk-overlay-container').forEach((element) => element.remove()),
  );

  it('opens a dialog named by its title, with the requested role', () => {
    const ref = TestBed.inject(AppDialog).open(TestDialog, {
      data: {},
      labelledBy: 'test-title',
      role: 'alertdialog',
    });
    TestBed.tick();

    const container = document.querySelector('[role=alertdialog]')!;
    expect(container.getAttribute('aria-labelledby')).toBe('test-title');
    expect(container.querySelector('#test-title')).not.toBeNull();
    ref.close();
  });
});
