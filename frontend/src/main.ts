// Entry point: starts the Angular app in the browser, with the root component (App) and the app-wide setup
// (app.config.ts).
import { bootstrapApplication } from '@angular/platform-browser';
import { appConfig } from './app/app.config';
import { App } from './app/app';

bootstrapApplication(App, appConfig).catch((err) => console.error(err));
