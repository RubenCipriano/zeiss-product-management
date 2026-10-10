// The app's ErrorHandler (logs every error and reports it) and the provider function that turns error reporting on.
import {
  afterNextRender,
  EnvironmentProviders,
  ErrorHandler,
  inject,
  makeEnvironmentProviders,
  provideAppInitializer,
  Service,
} from '@angular/core';
import { BrowserErrors } from './browser-errors';

/**
 * The browser's own `console.error`, taken before Faro wraps it to report console errors: the handler logs every
 * error and reports it itself, and must not report it a second time through the console.
 */
const logToConsole = console.error.bind(console);

/**
 * The app's ErrorHandler: the one place Angular sends every error it sees (in templates, lifecycle hooks, effects)
 * and, through `provideBrowserGlobalErrorListeners`, every uncaught error and unhandled promise rejection. This one logs
 * each error to the console, as Angular's default does, and also reports it (BrowserErrors).
 */
@Service({ autoProvided: false })
export class ReportingErrorHandler implements ErrorHandler {
  private readonly browserErrors = inject(BrowserErrors);

  handleError(error: unknown): void {
    logToConsole('ERROR', error);
    this.browserErrors.report(error);
  }
}

/**
 * Turns on error reporting (used in app.config.ts): replaces Angular's default ErrorHandler with the one above, and
 * starts the reporting SDK after the first render so it never delays the page.
 */
export function provideBrowserErrorReporting(): EnvironmentProviders {
  return makeEnvironmentProviders([
    { provide: ErrorHandler, useClass: ReportingErrorHandler },
    provideAppInitializer(() => {
      const browserErrors = inject(BrowserErrors);
      afterNextRender(() => void browserErrors.start());
    }),
  ]);
}
