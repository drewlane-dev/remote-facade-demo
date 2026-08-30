import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    // Calls go to /api on this same origin. nginx proxies them to the API
    // container, so the browser never makes a cross-origin request and the
    // demo needs no CORS configuration at all.
    provideHttpClient(),
  ],
};
