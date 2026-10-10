import { ErrorHandler } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { BrowserErrors } from './browser-errors';
import { provideBrowserErrorReporting } from './reporting-error-handler';

// Before the handler's module is loaded: it keeps the console.error of that moment.
const consoleError = vi.hoisted(() =>
  vi.spyOn(console, 'error').mockImplementation(() => undefined),
);

describe('ReportingErrorHandler', () => {
  const report = vi.fn();
  let handler: ErrorHandler;

  beforeEach(() => {
    report.mockReset();
    TestBed.configureTestingModule({
      providers: [
        provideBrowserErrorReporting(),
        { provide: BrowserErrors, useValue: { report, start: vi.fn() } },
      ],
    });
    handler = TestBed.inject(ErrorHandler);
  });

  afterAll(() => consoleError.mockRestore());

  it('replaces Angular’s error handler: logs every error it receives, and reports it', () => {
    const error = new Error('boom');

    handler.handleError(error);

    expect(consoleError).toHaveBeenCalledWith('ERROR', error);
    expect(report).toHaveBeenCalledWith(error);
  });
});
